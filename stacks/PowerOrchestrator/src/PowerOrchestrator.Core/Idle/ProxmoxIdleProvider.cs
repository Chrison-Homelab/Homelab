using Microsoft.Extensions.Logging;
using PowerOrchestrator.Core.Model;
using ProxmoxSharp;
using ProxmoxSharp.Api;

namespace PowerOrchestrator.Core.Idle;

/// <summary>
/// Node idle/online state via a single ProxmoxSharp cluster discovery: for each managed node,
/// is it online and how many guests are running. "Idle" (the sleep precondition) = zero running
/// guests. A node absent from the snapshot (or marked offline) is reported offline with 0 guests.
/// </summary>
public sealed class ProxmoxIdleProvider(ProxmoxApiClientFactory clientFactory, ILogger<ProxmoxIdleProvider> logger)
{
    public async Task<IReadOnlyList<NodeState>> GetAsync(
        IEnumerable<string> managedNodes, CancellationToken ct = default)
    {
        var client = clientFactory();
        if (client is null)
        {
            // No Proxmox creds — degrade gracefully rather than throwing every poll. Reporting
            // offline is safe: the policy treats away+offline as NoOp, never a spurious action.
            logger.LogWarning("No Proxmox credentials; reporting managed nodes as offline.");
            return managedNodes.Select(n => new NodeState(n, IsOnline: false, RunningGuests: 0)).ToList();
        }

        var snapshot = await new ProxmoxDiscovery(client).DiscoverAsync(ct).ConfigureAwait(false);

        var result = new List<NodeState>();
        foreach (var name in managedNodes)
        {
            var node = snapshot.Nodes.FirstOrDefault(n =>
                string.Equals(n.Node, name, StringComparison.OrdinalIgnoreCase));

            if (node is null)
            {
                result.Add(new NodeState(name, IsOnline: false, RunningGuests: 0));
                continue;
            }

            var online = string.Equals(node.Status, "online", StringComparison.OrdinalIgnoreCase);
            var running = node.Lxc.Count(g => IsRunning(g.Status)) + node.Qemu.Count(g => IsRunning(g.Status));
            result.Add(new NodeState(name, online, running));
        }

        logger.LogDebug("Idle scan: {States}",
            string.Join(", ", result.Select(s => $"{s.Name}={(s.IsOnline ? $"{s.RunningGuests} run" : "offline")}")));
        return result;
    }

    internal static bool IsRunning(string? status) =>
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Factory for a fresh ProxmoxApiClient, or null when no Proxmox credentials are configured.
/// A delegate (not a singleton client) so a transient auth/TLS failure on one poll doesn't
/// poison every subsequent poll.
/// </summary>
public delegate ProxmoxApiClient? ProxmoxApiClientFactory();
