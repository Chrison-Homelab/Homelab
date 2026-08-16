using Homelab.Infrastructure.Shapes;
using Homelab.Infrastructure.Unifi;
using UnifiSharp.Legacy;

namespace Homelab.Infrastructure.Converge;

// Applies the DHCP reservations a guest shape declares (#416), on the reconcile pass
// the runner already runs straight after create.
//
// The MAC is read off the LIVE guest rather than declared. That is the whole reason
// reservations can live on the guest shape at all: `pct config <ctid>` knows the MAC
// Proxmox generated, so a shape never has to predict one, and the existing decision not
// to pin hwaddr (see homeassistant.lxc.yaml) stays intact.
//
// Ordering matters: this runs AFTER CtConfigReconciler, so a NIC the shape just added
// already exists and has a MAC to reserve against.
//
// Degrades quietly. No UniFi credentials, or a controller that can't be reached, is a
// SKIPPED — not a failure. Converging a stack must not require the network controller
// to be up, and the reservation is a convergence detail, not a precondition for the
// guest itself.
public sealed class UnifiReservationReconciler : IDisposable
{
#pragma warning disable CS0618 // UnifiLegacyClient is intentionally obsolete (ADR-0003)
    private readonly INodeExec _exec;
    private readonly UnifiLegacyClient? _client;
    private IReadOnlyList<UnifiNetwork>? _networks;
    private IReadOnlyList<UnifiUser>? _users;

    public UnifiReservationReconciler(INodeExec exec, UnifiLegacyOptions? options)
    {
        _exec = exec;
        _client = options is null ? null : new UnifiLegacyClient(options);
    }

    /// <summary>True when there is no controller configured — callers should skip silently.</summary>
    public bool Unconfigured => _client is null;

    /// <summary>
    /// Declared reservations for a shape, paired with the netN index each belongs to.
    /// <c>spec.network</c> is the primary NIC (net0); <c>spec.networks[i]</c> is netI.
    /// </summary>
    public static IEnumerable<(int Index, ReservationSpec Reservation, int? Vlan)> Declared(Shape s)
    {
        var sp = s.Spec;
        if (sp.Network?.Reservation is { } primary)
        {
            yield return (0, primary, sp.Network.Vlan);
        }

        for (var i = 0; i < sp.Networks.Count; i++)
        {
            if (sp.Networks[i].Reservation is { } r)
            {
                yield return (i, r, sp.Networks[i].Tag);
            }
        }
    }

    /// <summary>Dry-run description, for `converge` without --apply.</summary>
    public static IEnumerable<string> PlanSteps(Shape s)
    {
        foreach (var (index, r, _) in Declared(s))
        {
            yield return r.IsParked
                ? $"net{index}: reservation {r.FixedIp} is PARKED ({r.Parked}) — reported, never written"
                : $"net{index}: reconcile the UniFi reservation → {r.FixedIp}"
                  + (string.IsNullOrWhiteSpace(r.LocalDnsRecord) ? "" : $" + DNS {r.LocalDnsRecord}");
        }
    }

    public async Task<ApplyResult> ReconcileAsync(Shape s, bool apply, CancellationToken ct = default)
    {
        var declared = Declared(s).ToList();
        if (declared.Count == 0)
        {
            return ApplyResult.NoChange("no reservations declared");
        }

        if (_client is null)
        {
            return ApplyResult.Skipped(
                $"{declared.Count} reservation(s) declared, but no UniFi credentials — set UNIFI_API_KEY + UNIFI_LOCAL_HOST");
        }

        var sp = s.Spec;
        if (sp.Node is not { } node || sp.Ctid is not { } ctid)
        {
            return ApplyResult.Skipped("no node/ctid — cannot read the live MAC");
        }

        // One `pct config` read serves every interface on this member.
        var read = await _exec.OnNodeAsync(node, $"pct config {ctid}", ct);
        if (!read.Ok)
        {
            return ApplyResult.Failed($"pct config failed: {read.Stderr}");
        }
        var cfg = ParseConfig(read.Stdout);

        List<UnifiNetwork> networks;
        List<UnifiUser> users;
        try
        {
            _networks ??= await _client.ListNetworksAsync(ct).ConfigureAwait(false);
            _users ??= await _client.ListUsersAsync(ct).ConfigureAwait(false);
            networks = [.. _networks];
            users = [.. _users];
        }
        catch (Exception ex)
        {
            return ApplyResult.Skipped($"UniFi controller unreachable ({ex.Message}) — reservations not reconciled");
        }

        var notes = new List<string>();
        var wrote = 0;
        var blocked = 0;

        foreach (var (index, spec, vlan) in declared)
        {
            var mac = LxcNet.Parse(cfg.GetValueOrDefault($"net{index}")).GetValueOrDefault("hwaddr");
            var desired = new DesiredReservation(
                Member: s.Metadata.Name,
                Mac: mac ?? "",
                FixedIp: spec.FixedIp ?? "",
                LocalDnsRecord: spec.LocalDnsRecord,
                Name: string.IsNullOrWhiteSpace(spec.Name) ? $"{s.Metadata.Name} (CT {ctid})" : spec.Name!,
                NetworkId: UnifiReservations.NetworkIdForVlan(networks, vlan),
                Parked: spec.Parked);

            var item = UnifiReservations.Plan(desired, users);
            switch (item.Action)
            {
                case ReservationAction.NoChange:
                    notes.Add($"net{index} {desired.FixedIp} ok");
                    break;

                case ReservationAction.Parked:
                    notes.Add($"net{index} {desired.FixedIp} PARKED ({item.Reason})");
                    break;

                case ReservationAction.Blocked:
                    notes.Add($"net{index} BLOCKED — {item.Reason}");
                    blocked++;
                    break;

                case ReservationAction.Create:
                    notes.Add($"net{index} {desired.FixedIp} {(apply ? "created" : "would create")} for {desired.Mac}");
                    if (apply)
                    {
                        await CreateOrAdoptAsync(desired, ct).ConfigureAwait(false);
                        wrote++;
                    }
                    break;

                case ReservationAction.Update:
                    notes.Add($"net{index} {(apply ? "corrected" : "DRIFTED")}: {string.Join("; ", item.Changes)}");
                    if (apply)
                    {
                        await _client.UpdateUserAsync(item.LiveId!, UnifiReservations.ToUser(desired), ct)
                            .ConfigureAwait(false);
                        wrote++;
                    }
                    break;
            }
        }

        // Live state changed under us — drop the caches so a later member re-reads.
        if (wrote > 0)
        {
            _users = null;
        }

        var message = string.Join(", ", notes);
        if (blocked > 0)
        {
            return ApplyResult.Failed(message);
        }

        return wrote > 0 ? ApplyResult.Applied(message) : ApplyResult.NoChange(message);
    }

    // A MAC the controller has never seen has no row to PUT, so it needs a POST. Any
    // guest that has taken a lease already has one, which is why this is the rare path.
    private async Task CreateOrAdoptAsync(DesiredReservation desired, CancellationToken ct)
    {
        var body = UnifiReservations.ToUser(desired) with { Mac = UnifiReservations.NormalizeMac(desired.Mac) };
        await _client!.CreateUserAsync(body, ct).ConfigureAwait(false);
    }

    // `pct config` is `key: value` per line — same parse as CtConfigReconciler.
    private static Dictionary<string, string> ParseConfig(string stdout)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            if (key.Length > 0) map[key] = line[(colon + 1)..].Trim();
        }
        return map;
    }

    public void Dispose() => _client?.Dispose();
#pragma warning restore CS0618
}
