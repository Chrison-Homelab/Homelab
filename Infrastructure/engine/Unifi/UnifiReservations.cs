using System.Net;
using UnifiSharp.Legacy;

namespace Homelab.Infrastructure.Unifi;

// Reconciles UniFi DHCP reservations declared on the guest shapes (#416) against the
// controller's known-client list. Pure planning lives here; the I/O (resolving a live
// MAC, mapping a VLAN to a network id, writing) is the reconciler's job.
//
// Why this exists: 22 reservations were hand-made, five shapes across four stacks
// carried "⚠ needs a DHCP reservation" comments, and nothing detected a lost or edited
// one. Six of them now carry a `local_dns_record`, so the hand-made object is not just
// an address any more — it is the name services are targeted by.
//
// THREE guardrails, all load-bearing:
//
//  1. ADD-ONLY BY MAC. The controller holds one row per MAC it has ever seen — 242 of
//     them against 19 reservations. Every write is keyed to a MAC some shape declares;
//     a row we did not declare is never touched, and never deleted. Undeclared
//     reservations are surfaced as orphan candidates for a human, not pruned.
//
//  2. PARKED IS NOT DRIFT. A reservation held for a deliberately-stopped guest (a
//     rollback VM, say) is indistinguishable from an orphan unless the shape says so.
//     `parked:` makes converge report and skip, so it neither flaps nor resurrects.
//
//  3. RECONCILE, NOT CREATE-IF-MISSING. Drift IS the failure mode here — a reservation
//     silently re-pointed at another address, or its DNS name dropped, is exactly what
//     this is meant to catch. Matching on existence alone would report success forever.

/// <summary>One reservation a shape asks for, with the MAC already resolved off the live guest.</summary>
public sealed record DesiredReservation(
    string Member,
    string Mac,
    string FixedIp,
    string? LocalDnsRecord,
    string Name,
    string? NetworkId,
    string? Parked)
{
    public bool IsParked => !string.IsNullOrWhiteSpace(Parked);
}

public enum ReservationAction
{
    /// <summary>Live matches the shape.</summary>
    NoChange,
    /// <summary>No reservation on this MAC yet.</summary>
    Create,
    /// <summary>Present but drifted from the shape.</summary>
    Update,
    /// <summary>Declared <c>parked:</c> — reported, never written.</summary>
    Parked,
    /// <summary>Can't be planned (no MAC, or the VLAN maps to no UniFi network).</summary>
    Blocked,
}

/// <summary>A planned action for one declared reservation. <see cref="Changes"/> is empty unless drifted.</summary>
public sealed record ReservationPlanItem(
    DesiredReservation Desired,
    ReservationAction Action,
    IReadOnlyList<string> Changes,
    string? LiveId,
    string? Reason = null);

/// <summary>A reservation on the controller that no shape declares. Reported only — never deleted.</summary>
public sealed record OrphanCandidate(string Mac, string? Name, string? FixedIp, string? LocalDnsRecord);

public static class UnifiReservations
{
    /// <summary>
    /// Plan one declared reservation against the controller's known clients. Pure —
    /// the same inputs always give the same answer, which is what makes it testable
    /// without a controller.
    /// </summary>
    public static ReservationPlanItem Plan(DesiredReservation desired, IEnumerable<UnifiUser> live)
    {
        if (desired.IsParked)
        {
            return new(desired, ReservationAction.Parked, [], LiveId: null, Reason: desired.Parked);
        }

        if (string.IsNullOrWhiteSpace(desired.Mac))
        {
            return new(desired, ReservationAction.Blocked, [], null,
                "no live MAC for the interface — is the guest created?");
        }

        if (string.IsNullOrWhiteSpace(desired.NetworkId))
        {
            return new(desired, ReservationAction.Blocked, [], null,
                "the interface's VLAN matches no UniFi network");
        }

        var match = live.FirstOrDefault(u => MacEquals(u.Mac, desired.Mac));
        if (match is null)
        {
            return new(desired, ReservationAction.Create, [], null);
        }

        var changes = Drift(match, desired);
        return changes.Count == 0
            ? new(desired, ReservationAction.NoChange, [], match.Id)
            : new(desired, ReservationAction.Update, changes, match.Id);
    }

    /// <summary>
    /// Field-by-field drift between a live known client and the shape. Returns a
    /// human-readable change per drifted field, empty when they agree.
    /// </summary>
    public static IReadOnlyList<string> Drift(UnifiUser live, DesiredReservation desired)
    {
        var changes = new List<string>();

        if (live.UseFixedIp != true)
        {
            changes.Add("use_fixedip: false → true");
        }

        if (!IpEquals(live.FixedIp, desired.FixedIp))
        {
            changes.Add($"fixed_ip: {Show(live.FixedIp)} → {desired.FixedIp}");
        }

        if (!string.IsNullOrEmpty(desired.NetworkId) &&
            !string.Equals(live.NetworkId, desired.NetworkId, StringComparison.Ordinal))
        {
            changes.Add($"network_id: {Show(live.NetworkId)} → {desired.NetworkId}");
        }

        // A shape that doesn't ask for a DNS name doesn't claim the field either — the
        // record may have been set by hand for a reason, and blanking it on the next
        // converge would be a silent, load-bearing deletion.
        if (!string.IsNullOrWhiteSpace(desired.LocalDnsRecord))
        {
            if (!DnsEquals(live.LocalDnsRecord, desired.LocalDnsRecord))
            {
                changes.Add($"local_dns_record: {Show(live.LocalDnsRecord)} → {desired.LocalDnsRecord}");
            }
            else if (live.LocalDnsRecordEnabled != true)
            {
                changes.Add("local_dns_record_enabled: false → true");
            }
        }

        if (!string.IsNullOrWhiteSpace(desired.Name) &&
            !string.Equals(live.Name, desired.Name, StringComparison.Ordinal))
        {
            changes.Add($"name: {Show(live.Name)} → {desired.Name}");
        }

        return changes;
    }

    /// <summary>
    /// The reservations on the controller that no shape accounts for, matched on EITHER
    /// identity: a live row counts as declared if its MAC or its fixed IP appears in the
    /// shapes. Two keys because the report has to work from the shapes alone — they
    /// declare an address but never a MAC, since the MAC only exists on the live guest.
    /// <para>Report-only, deliberately. This is the signal that would have caught CT
    /// 4100's reservation outliving its guest, but an undeclared reservation is equally
    /// likely to be a parked guest or something not managed here, so removing one stays
    /// a human decision.</para>
    /// </summary>
    public static IReadOnlyList<OrphanCandidate> OrphanCandidates(
        IEnumerable<UnifiUser> live,
        IEnumerable<string>? declaredMacs = null,
        IEnumerable<string>? declaredIps = null)
    {
        var macs = new HashSet<string>(
            (declaredMacs ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(NormalizeMac),
            StringComparer.Ordinal);
        var ips = (declaredIps ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();

        return live
            .Where(u => u.UseFixedIp == true)
            .Where(u => !macs.Contains(NormalizeMac(u.Mac ?? "")))
            .Where(u => !ips.Any(i => IpEquals(i, u.FixedIp)))
            .Select(u => new OrphanCandidate(u.Mac ?? "", u.Name, u.FixedIp, u.LocalDnsRecord))
            .OrderBy(o => o.FixedIp, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The partial-update body for a reservation — only the fields we own.</summary>
    public static UnifiUser ToUser(DesiredReservation d) => new()
    {
        UseFixedIp = true,
        FixedIp = d.FixedIp,
        NetworkId = d.NetworkId,
        Name = string.IsNullOrWhiteSpace(d.Name) ? null : d.Name,
        LocalDnsRecord = string.IsNullOrWhiteSpace(d.LocalDnsRecord) ? null : d.LocalDnsRecord,
        LocalDnsRecordEnabled = string.IsNullOrWhiteSpace(d.LocalDnsRecord) ? null : true,
    };

    /// <summary>
    /// Map a VLAN tag to the UniFi network's <c>_id</c>. Untagged (null) resolves to
    /// the network with no VLAN — the default LAN.
    /// </summary>
    public static string? NetworkIdForVlan(IEnumerable<UnifiNetwork> networks, int? vlan)
    {
        foreach (var n in networks)
        {
            if (string.Equals(n.Purpose, "wan", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var liveVlan = int.TryParse(n.Vlan, out var v) ? v : (int?)null;
            if (liveVlan == vlan)
            {
                return n.Id;
            }
        }

        return null;
    }

    // ---- comparison helpers ----
    // Normalised so a cosmetic difference (case, an IPv6 spelling, a trailing dot)
    // never reads as drift and re-writes the controller on every single converge.

    public static string NormalizeMac(string mac) =>
        mac.Trim().Replace("-", ":", StringComparison.Ordinal).ToLowerInvariant();

    public static bool MacEquals(string? a, string? b) =>
        a is not null && b is not null && NormalizeMac(a) == NormalizeMac(b);

    public static bool IpEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);
        }

        return IPAddress.TryParse(a.Trim(), out var ia) && IPAddress.TryParse(b.Trim(), out var ib)
            ? ia.Equals(ib)
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool DnsEquals(string? a, string? b) =>
        string.Equals(
            a?.Trim().TrimEnd('.') ?? "", b?.Trim().TrimEnd('.') ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Show(string? s) => string.IsNullOrWhiteSpace(s) ? "(unset)" : s;
}
