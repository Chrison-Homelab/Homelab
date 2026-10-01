using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// OpenBao (DevOps CT 3007, #609) — the secrets store. This provisioner never holds, reads
// or writes a key. It owns exactly one non-secret file, the server config, and otherwise ASSERTS.
//
// THE SERVER CONFIG IS OURS because the packaged one cannot start. OpenBao 2.7.0 removed the
// `file` storage backend, but the 2.7.0 .deb still ships /etc/openbao/openbao.hcl with
// `storage "file"`, so the unit dies with "unknown storage type file". community-scripts'
// installer then fails at `systemctl enable --now` BEFORE its init step: observed on CT 3007,
// 2026-10-01; upstream fix community-scripts/ProxmoxVE#17548 still open at the time. We render
// integrated storage (raft) instead, which is what upstream moved to anyway, and keep it rendered
// so a package upgrade that resets the file cannot silently break the store again.
//
// Why assert-only: community-scripts' installer inits with a single key share and writes
// BAO_UNSEAL_KEY and BAO_ROOT_TOKEN in plaintext into /etc/openbao/openbao.env, plus a
// systemd drop-in that unseals from that file on every start. The seal is then decorative —
// the CT disk and every PBS backup of it open the store. Fixing that means rekeying and
// moving the shares into Bitwarden, and a converge is the wrong actor for it: its output
// lands in CI logs, and it cannot hand a share to a human safely. So the fix is a one-off,
// workstation-side script (stacks/DevOps/tools/openbao-bootstrap.sh), and this provisioner
// makes sure a converge can never report success over the unfixed state.
//
// Reported, in order:
//   FAILED  plaintext key material or the auto-unseal drop-in is still on disk
//   FAILED  not initialised, or the seal is not the declared keyShares/keyThreshold
//   APPLIED hardened + unsealed (the message says so)
//   APPLIED hardened + SEALED — a normal state after any restart, by design (manual unseal).
//           Not a failure: a converge must not go red because nobody has typed two shares yet.
//           Alerting on "sealed for too long" belongs in monitoring, not here.
public sealed class OpenBaoProvisioner : IAppProvisioner
{
    public string App => "openbao";

    internal const string EnvFile = "/etc/openbao/openbao.env";
    internal const string UnsealDropIn = "/etc/systemd/system/openbao.service.d/unseal.conf";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var (n, t) = DeclaredSeal(s);
        yield return $"render {ConfigFile} (raft storage, TLS :8200, api_addr {ApiAddr(s)}) and keep openbao enabled + running";
        yield return $"assert no plaintext unseal key / root token in {EnvFile} and no auto-unseal drop-in";
        yield return $"assert initialised with a {t}-of-{n} seal (bootstrap: stacks/DevOps/tools/openbao-bootstrap.sh)";
        yield return "report seal state — SEALED after a restart is expected (manual unseal from Bitwarden)";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        // Config first: nothing below can be checked while the server cannot start. A config
        // change restarts the server, which SEALS it — said plainly in the result.
        var cfg = await ctx.Exec.InContainerAsync(node, ctid, RenderConfigScript(RenderConfig(s)));
        if (cfg.ExitCode != 0) return ApplyResult.Failed($"config render / start failed: {cfg.Stderr.Trim()}");
        var restarted = cfg.Stdout.Contains("RESTARTED");

        var disk = await ctx.Exec.InContainerAsync(node, ctid, HardeningProbe);
        if (disk.ExitCode != 0) return ApplyResult.Failed($"hardening probe failed: {disk.Stderr.Trim()}");
        if (UnhardenedFindings(disk.Stdout) is { Count: > 0 } found)
            return ApplyResult.Failed(
                "UNHARDENED — " + string.Join("; ", found) +
                ". The seal is decorative until this is fixed: run stacks/DevOps/tools/openbao-bootstrap.sh from the workstation.");

        var st = await ctx.Exec.InContainerAsync(node, ctid, "curl -fsSk https://127.0.0.1:8200/v1/sys/seal-status");
        if (st.ExitCode != 0) return ApplyResult.Failed($"seal-status unreachable: {st.Stderr.Trim()}");
        var r = Evaluate(st.Stdout, DeclaredSeal(s));
        return restarted && r.Outcome == ApplyOutcome.Applied
            ? ApplyResult.Applied($"{ConfigFile} changed → restarted; " + r.Message)
            : r;
    }

    internal const string ConfigFile = "/etc/openbao/openbao.hcl";

    // The reservation's DNS name is the address clients use, and the one the bootstrap puts in
    // the TLS SAN, so api_addr follows it rather than a hard-coded name.
    internal static string ApiAddr(Shape s) =>
        $"https://{s.Spec.Network?.Reservation?.LocalDnsRecord ?? "127.0.0.1"}:8200";

    internal static string RenderConfig(Shape s) => string.Join("\n", new[]
    {
        "# homelab-managed by the openbao provisioner (#609). Edits here are overwritten on converge.",
        "ui = true",
        "",
        "storage \"raft\" {",
        "  path    = \"/opt/openbao/data\"",
        $"  node_id = \"openbao-{s.Spec.Ctid}\"",
        "}",
        "",
        "listener \"tcp\" {",
        "  address       = \"0.0.0.0:8200\"",
        "  tls_cert_file = \"/opt/openbao/tls/tls.crt\"",
        "  tls_key_file  = \"/opt/openbao/tls/tls.key\"",
        "}",
        "",
        $"api_addr     = \"{ApiAddr(s)}\"",
        "cluster_addr = \"https://127.0.0.1:8201\"",
        "",
    });

    // Write only on drift (so a no-op converge never restarts, i.e. never re-seals), then make
    // sure the unit is enabled and active. Prints RESTARTED when it restarted the server.
    internal static string RenderConfigScript(string config)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(config));
        return "set -e; t=$(mktemp); echo " + b64 + " | base64 -d > \"$t\"; " +
               "if ! cmp -s \"$t\" " + ConfigFile + "; then install -o openbao -g openbao -m 640 \"$t\" " + ConfigFile + "; " +
               "systemctl reset-failed openbao 2>/dev/null || true; systemctl enable -q openbao; systemctl restart openbao; echo RESTARTED; " +
               "else systemctl enable -q openbao; systemctl is-active -q openbao || { systemctl reset-failed openbao 2>/dev/null || true; systemctl start openbao; echo RESTARTED; }; fi; " +
               "rm -f \"$t\"; for i in $(seq 1 30); do curl -fsSk -o /dev/null https://127.0.0.1:8200/v1/sys/seal-status && exit 0; sleep 1; done; " +
               "journalctl -u openbao -n 20 --no-pager >&2; exit 1";
    }

    // One line per finding; an empty output means hardened. Reads names only — `grep -c` on
    // the variable names never prints a value into converge output.
    internal const string HardeningProbe =
        "k=$(grep -cE '^BAO_(UNSEAL_KEY|ROOT_TOKEN)=' " + EnvFile + " 2>/dev/null || true); " +
        "[ \"${k:-0}\" != 0 ] && echo \"plaintext key material in " + EnvFile + " ($k line(s))\"; " +
        "[ -e " + UnsealDropIn + " ] && echo \"auto-unseal drop-in present (" + UnsealDropIn + ")\"; " +
        "[ -e /etc/openbao/openbao-init.json ] && echo 'init output left on disk (/etc/openbao/openbao-init.json)'; " +
        "true";

    internal static List<string> UnhardenedFindings(string probeStdout) =>
        probeStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal static (int Shares, int Threshold) DeclaredSeal(Shape s)
    {
        int I(string k, int d) => s.Spec.Config.TryGetValue(k, out var v) && int.TryParse(v?.ToString(), out var i) ? i : d;
        return (I("keyShares", 3), I("keyThreshold", 2));
    }

    internal static ApplyResult Evaluate(string sealStatusJson, (int Shares, int Threshold) want)
    {
        using var doc = JsonDocument.Parse(sealStatusJson);
        var r = doc.RootElement;
        if (!r.GetProperty("initialized").GetBoolean())
            return ApplyResult.Failed("not initialised — run stacks/DevOps/tools/openbao-bootstrap.sh");
        var n = r.GetProperty("n").GetInt32();
        var t = r.GetProperty("t").GetInt32();
        if (n != want.Shares || t != want.Threshold)
            return ApplyResult.Failed($"seal is {t}-of-{n}, declared {want.Threshold}-of-{want.Shares} — rekey via openbao-bootstrap.sh");
        return r.GetProperty("sealed").GetBoolean()
            ? ApplyResult.Applied($"hardened, {t}-of-{n} seal — ⚠ SEALED: unseal with {t} shares from Bitwarden (`bao operator unseal` in CT)")
            : ApplyResult.Applied($"hardened, {t}-of-{n} seal, unsealed");
    }
}
