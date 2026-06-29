using Microsoft.Extensions.Logging;
using PowerOrchestrator.Core.Config;
using PowerOrchestrator.Core.Idle;
using ProxmoxSharp;
using ProxmoxSharp.Lxc;
using ProxmoxSharp.Vm;

namespace PowerOrchestrator.Core.Power;

/// <summary>
/// Performs the real power actions, all in C#:
/// <list type="bullet">
/// <item><b>Wake</b> — a WoL magic packet (no API; works while the node is in S5).</item>
/// <item><b>Sleep</b> — gracefully shut down the node's running guests via the ProxmoxSharp
/// write path (PctWriter/QemuWriter), then <c>poweroff</c> the host over SSH. This is the
/// exact sequence proven by hand on desktop-01.</item>
/// </list>
/// These are the operations the manual control endpoints call. The automatic loop only invokes
/// them when armed; in dry-run it never does.
/// </summary>
public sealed class NodePowerController(
    OrchestratorOptions options,
    Func<ProxmoxClientOptions?> pveFactory,
    SshExec ssh,
    ILogger<NodePowerController> logger)
{
    /// <summary>Send a WoL magic packet to the node's NIC.</summary>
    public async Task WakeAsync(string node, CancellationToken ct = default)
    {
        var mac = ResolveMac(node);
        logger.LogInformation("WoL → {Node} ({Mac}) via {Broadcast}:{Port}",
            node, mac, options.WolBroadcast, options.WolPort);
        await WakeOnLan.SendAsync(mac, options.WolBroadcast, options.WolPort, ct).ConfigureAwait(false);
    }

    /// <summary>Gracefully stop the node's guests, then power the host off. Returns guests stopped.</summary>
    public async Task<int> SleepAsync(string node, CancellationToken ct = default)
    {
        var stopped = await StopGuestsAsync(node, ct).ConfigureAwait(false);

        var host = ResolveAddress(node);
        logger.LogInformation("poweroff {Node} ({Host}) — {Stopped} guest(s) stopped", node, host, stopped);

        // Detach so ssh returns immediately rather than hanging as the host tears down the link
        // (the proven manual incantation).
        var result = await ssh.RunAsync(host, "nohup systemctl poweroff >/dev/null 2>&1 &", ct)
            .ConfigureAwait(false);
        if (!result.Ok)
            throw new InvalidOperationException(
                $"poweroff {node} ({host}) failed (exit {result.ExitCode}): {result.Stderr.Trim()}");

        return stopped;
    }

    private async Task<int> StopGuestsAsync(string node, CancellationToken ct)
    {
        var pve = pveFactory();
        if (pve is null)
        {
            logger.LogWarning(
                "No Proxmox credentials; skipping graceful guest shutdown for {Node} (host poweroff will stop them).",
                node);
            return 0;
        }

        var snapshot = await new ProxmoxDiscovery(ProxmoxApi.Create(pve)).DiscoverAsync(ct).ConfigureAwait(false);
        var target = snapshot.Nodes.FirstOrDefault(n =>
            string.Equals(n.Node, node, StringComparison.OrdinalIgnoreCase));
        if (target is null) return 0;

        var pct = PctWriter.Create(pve);
        var qemu = QemuWriter.Create(pve);
        var stopped = 0;

        foreach (var g in target.Lxc.Where(g => ProxmoxIdleProvider.IsRunning(g.Status) && g.VmId is not null))
        {
            logger.LogInformation("shutdown LXC {Vmid} ({Name}) on {Node}", g.VmId, g.Name, node);
            await pct.ShutdownAsync(node, (int)g.VmId!.Value, forceStop: true, timeout: 60, ct).ConfigureAwait(false);
            stopped++;
        }
        foreach (var g in target.Qemu.Where(g => ProxmoxIdleProvider.IsRunning(g.Status) && g.VmId is not null))
        {
            logger.LogInformation("shutdown VM {Vmid} ({Name}) on {Node}", g.VmId, g.Name, node);
            await qemu.ShutdownAsync(node, (int)g.VmId!.Value, ct).ConfigureAwait(false);
            stopped++;
        }
        return stopped;
    }

    public string ResolveMac(string node) =>
        options.NodeMacs.TryGetValue(node, out var mac)
            ? mac
            : throw new InvalidOperationException($"No WoL MAC registered for node '{node}'.");

    public string ResolveAddress(string node) =>
        options.NodeAddresses.TryGetValue(node, out var addr) ? addr : node;
}
