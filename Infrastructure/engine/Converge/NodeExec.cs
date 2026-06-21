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
        // accept-new: the runner reaches nodes by IP (see Resolve) and won't have the
        // IP in known_hosts on first contact; BatchMode would otherwise fail. TOFU on
        // a trusted LAN — a CHANGED key is still rejected.
        => RunAsync("ssh", new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
            $"{_sshUser}@{Resolve(node)}", command }, ct);

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
