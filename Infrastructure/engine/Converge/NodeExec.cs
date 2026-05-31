using System.Diagnostics;

namespace Homelab.Infrastructure.Converge;

public sealed record ExecResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

// Runs commands on a Proxmox node over SSH (root@<node>, like the BL-013
// renderer), and inside a CT via `pct exec`. Used by provisioners at apply time.
public sealed class NodeExec
{
    private readonly string _sshUser;
    public NodeExec(string sshUser = "root") => _sshUser = sshUser;

    public Task<ExecResult> OnNodeAsync(string node, string command, CancellationToken ct = default)
        => RunAsync("ssh", new[] { "-o", "BatchMode=yes", $"{_sshUser}@{node}", command }, ct);

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
