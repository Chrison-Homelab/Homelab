using System.Diagnostics;

namespace Homelab.Infrastructure.Converge;

public sealed record ExecResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

// Command execution seam (issue #45). Lets provisioner idempotency be unit-tested
// with a fake exec — no live cluster / SSH needed. Additive: NodeExec is the
// real implementation; ConvergeContext.Exec is typed as this interface.
public interface INodeExec
{
    Task<ExecResult> OnNodeAsync(string node, string command, CancellationToken ct = default);
    Task<ExecResult> InContainerAsync(string node, string ctid, string command, CancellationToken ct = default);
}

// Runs commands on a Proxmox node over SSH (root@<node>, like the BL-013
// renderer), and inside a CT via `pct exec`. Used by provisioners at apply time.
public sealed class NodeExec : INodeExec
{
    private readonly string _sshUser;
    public NodeExec(string sshUser = "root") => _sshUser = sshUser;

    public Task<ExecResult> OnNodeAsync(string node, string command, CancellationToken ct = default)
        => RunAsync("ssh", BuildSshArgs(node, command), ct);

    // ssh args. accept-new: the runner reaches nodes by IP (see Resolve) and won't have
    // the IP in known_hosts on first contact; BatchMode would otherwise fail (TOFU on a
    // trusted LAN — a CHANGED key is still rejected). NODE_SSH_KEY: the self-hosted
    // runner runs jobs with a HOME that ISN'T the login home, so ssh can't find the
    // identity in ~/.ssh; point it at the key explicitly (IdentitiesOnly) + a sibling
    // known_hosts, decoupling auth from $HOME entirely. See issue #162.
    private string[] BuildSshArgs(string node, string command)
    {
        // Keepalives: a community-scripts create holds ONE ssh session open for the whole
        // template download + apt install — 30+ minutes on a cold template. Without keepalives
        // an idle-looking connection gets reset by something in between (observed 2026-07-26
        // provisioning CT 9900 from a laptop across VLANs: "Read from remote host ...
        // Connection reset by peer" after 31 minutes, with the CT actually created but the
        // result never returned, so converge reported CREATE FAILED for a create that worked).
        // 30s × 10 tolerates ~5 minutes of silence before giving up.
        var args = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "TCPKeepAlive=yes",
            "-o", "ServerAliveInterval=30",
            "-o", "ServerAliveCountMax=10",
        };
        var key = Environment.GetEnvironmentVariable("NODE_SSH_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            args.AddRange(new[] { "-i", key, "-o", "IdentitiesOnly=yes" });
            var dir = Path.GetDirectoryName(key);
            if (!string.IsNullOrEmpty(dir))
                args.AddRange(new[] { "-o", $"UserKnownHostsFile={Path.Combine(dir, "known_hosts")}" });
        }
        args.Add($"{_sshUser}@{Resolve(node)}");
        args.Add(command);
        return args.ToArray();
    }

    // Map a Proxmox node name to an SSH-reachable address. The self-hosted runner
    // sits on the node LAN but has no DNS for the short names (`hpe-01`), so an
    // explicit `NODE_ADDR_<NAME>` env override (e.g. NODE_ADDR_HPE_01=192.168.179.3)
    // sends SSH straight to the IP. With no override we fall back to the name, so
    // local runs that resolve via DNS/hosts keep working. See issue #162.
    public static string Resolve(string node)
    {
        var key = "NODE_ADDR_" + new string(node.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
        var addr = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(addr) ? node : addr.Trim();
    }

    public Task<ExecResult> InContainerAsync(string node, string ctid, string command, CancellationToken ct = default)
        // pct exec <ctid> -- bash -lc '<command>' — single-quoted on the remote.
        => OnNodeAsync(node, $"pct exec {ctid} -- bash -lc {Quote(command)}", ct);

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static async Task<ExecResult> RunAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {file}");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new ExecResult(p.ExitCode, stdout.Trim(), stderr.Trim());
    }
}
