using System.Diagnostics;
using System.Text;

namespace PowerOrchestrator.Core.Power;

/// <summary>Result of a remote command: exit code + captured streams.</summary>
public sealed record SshResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Minimal SSH command runner — shells out to the system <c>ssh</c>, mirroring the engine's
/// Infrastructure/engine/Converge/NodeExec.cs. Used for the host <c>poweroff</c> after the
/// node's guests have been gracefully stopped via the Proxmox API. Key-based, non-interactive.
/// </summary>
public sealed class SshExec(string user = "root", string? keyPath = null)
{
    public async Task<SshResult> RunAsync(string host, string command, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=10");
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("IdentitiesOnly=yes");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(keyPath);
        }
        psi.ArgumentList.Add($"{user}@{host}");
        psi.ArgumentList.Add(command);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start ssh");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = PumpAsync(p.StandardOutput, stdout, ct);
        var errTask = PumpAsync(p.StandardError, stderr, ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);

        return new SshResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task PumpAsync(TextReader reader, StringBuilder sink, CancellationToken ct)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            sink.Append(buffer, 0, read);
    }
}
