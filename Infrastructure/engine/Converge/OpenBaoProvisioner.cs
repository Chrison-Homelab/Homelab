using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// OpenBao (DevOps CT 3007, #609) — the secrets store. This provisioner ASSERTS; it never
// holds, reads or writes a key.
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
        yield return $"assert no plaintext unseal key / root token in {EnvFile} and no auto-unseal drop-in";
        yield return $"assert initialised with a {t}-of-{n} seal (bootstrap: stacks/DevOps/tools/openbao-bootstrap.sh)";
        yield return "report seal state — SEALED after a restart is expected (manual unseal from Bitwarden)";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        var disk = await ctx.Exec.InContainerAsync(node, ctid, HardeningProbe);
        if (disk.ExitCode != 0) return ApplyResult.Failed($"hardening probe failed: {disk.Stderr.Trim()}");
        if (UnhardenedFindings(disk.Stdout) is { Count: > 0 } found)
            return ApplyResult.Failed(
                "UNHARDENED — " + string.Join("; ", found) +
                ". The seal is decorative until this is fixed: run stacks/DevOps/tools/openbao-bootstrap.sh from the workstation.");

        var st = await ctx.Exec.InContainerAsync(node, ctid, "curl -fsSk https://127.0.0.1:8200/v1/sys/seal-status");
        if (st.ExitCode != 0) return ApplyResult.Failed($"seal-status unreachable: {st.Stderr.Trim()}");
        return Evaluate(st.Stdout, DeclaredSeal(s));
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
