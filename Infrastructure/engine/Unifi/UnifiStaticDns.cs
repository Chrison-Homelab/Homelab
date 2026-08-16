using UnifiSharp.Legacy;

namespace Homelab.Infrastructure.Unifi;

// Reconciles UniFi controller-local DNS records declared in network.yaml (#314/#419).
// Pure planning; the I/O is the caller's.
//
// These records are how a name resolves ON THE LAN, with no public zone involved. Two
// jobs, one mechanism:
//
//   * A WILDCARD per Pangolin-fronted zone, so `*.lab.chrison.dev` resolves straight to
//     Traefik instead of the home WAN IP. Without it, LAN access to every admin UI is a
//     NAT hairpin back through the public port-forward — meaning the "reversible exit"
//     of closing :443 would take the surface down internally too (#419).
//   * A PER-SERVICE record pointing an internal name at whatever serves it (#314).
//
// ADD-ONLY, like everything else that touches the shared controller: a record this file
// does not declare is never created, edited or deleted. Undeclared records are surfaced
// for a human — the controller carries a dozen belonging to other things.
//
// RECONCILED, not create-if-missing. A record that exists but points somewhere else is
// the failure this is meant to catch, and it is invisible from the shape alone.

/// <summary>One declared controller-local DNS record.</summary>
public sealed class StaticDnsSpec
{
    /// <summary>The name, e.g. <c>*.lab.chrison.dev</c> or <c>prometheus.homelab.chrison.internal</c>.</summary>
    public string Name { get; set; } = "";
    /// <summary>The answer — an address for A/AAAA, a target for CNAME.</summary>
    public string Value { get; set; } = "";
    /// <summary><c>A</c> (default), <c>AAAA</c>, <c>CNAME</c>, <c>TXT</c>.</summary>
    public string Type { get; set; } = "A";
    public int? Ttl { get; set; }
    public bool Enabled { get; set; } = true;
}

public enum StaticDnsAction { NoChange, Create, Update }

public sealed record StaticDnsPlanItem(
    StaticDnsSpec Desired, StaticDnsAction Action, IReadOnlyList<string> Changes, string? LiveId);

/// <summary>A controller record no shape declares. Reported only — never removed.</summary>
public sealed record UndeclaredDnsRecord(string Key, string Type, string Value);

public static class UnifiStaticDns
{
    /// <summary>
    /// Plan one declared record against the controller's live set. Matching is by NAME —
    /// a name can only answer one way, so a second record for the same key is drift, not
    /// an addition.
    /// </summary>
    public static StaticDnsPlanItem Plan(StaticDnsSpec desired, IEnumerable<UnifiStaticDnsRecord> live)
    {
        var match = live.FirstOrDefault(
            r => NameEquals(r.Key, desired.Name)
                 && string.Equals(r.RecordType, desired.Type, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return new(desired, StaticDnsAction.Create, [], null);
        }

        var changes = Drift(match, desired);
        return changes.Count == 0
            ? new(desired, StaticDnsAction.NoChange, [], match.Id)
            : new(desired, StaticDnsAction.Update, changes, match.Id);
    }

    /// <summary>Field-by-field drift between a live record and the shape.</summary>
    public static IReadOnlyList<string> Drift(UnifiStaticDnsRecord live, StaticDnsSpec desired)
    {
        var changes = new List<string>();

        if (!ValueEquals(live.Value, desired.Value, desired.Type))
        {
            changes.Add($"value: {live.Value} → {desired.Value}");
        }

        if (live.Enabled != desired.Enabled)
        {
            changes.Add($"enabled: {live.Enabled} → {desired.Enabled}");
        }

        // TTL is only claimed when the shape states one — the controller's default is fine
        // and rewriting it on every converge would be noise.
        if (desired.Ttl is { } ttl && live.Ttl != ttl)
        {
            changes.Add($"ttl: {live.Ttl} → {ttl}");
        }

        return changes;
    }

    /// <summary>
    /// The full record to send. The v2 API rejects partial updates, so this is always the
    /// complete desired state — carrying the live record's untouched fields forward when
    /// updating, rather than resetting them to defaults.
    /// </summary>
    public static UnifiStaticDnsRecord ToRecord(StaticDnsSpec s, UnifiStaticDnsRecord? live = null) => new()
    {
        Id = live?.Id,
        Key = s.Name.Trim(),
        RecordType = s.Type.ToUpperInvariant(),
        Value = s.Value.Trim(),
        Enabled = s.Enabled,
        Ttl = s.Ttl ?? live?.Ttl ?? 300,
        Port = live?.Port ?? 0,
        Priority = live?.Priority ?? 0,
        Weight = live?.Weight ?? 0,
    };

    /// <summary>
    /// Live records no shape declares. Report-only: the controller carries records owned
    /// by other things (the Azure Lab's <c>*.topaz.local.dev</c> set, the node names), and
    /// deleting one because this file is silent about it is exactly what add-only forbids.
    /// </summary>
    public static IReadOnlyList<UndeclaredDnsRecord> Undeclared(
        IEnumerable<UnifiStaticDnsRecord> live, IEnumerable<StaticDnsSpec> declared)
    {
        var names = declared.Select(d => Normalize(d.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return live
            .Where(r => !names.Contains(Normalize(r.Key)))
            .Select(r => new UndeclaredDnsRecord(r.Key, r.RecordType, r.Value))
            .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- comparison helpers ----

    private static string Normalize(string? s) => (s ?? "").Trim().TrimEnd('.').ToLowerInvariant();

    public static bool NameEquals(string? a, string? b) => Normalize(a) == Normalize(b);

    // A/AAAA answers are addresses, so compare them by value — 10.10.000.13 is 10.10.0.13.
    // Anything else is a name or free text and compares as a normalized string.
    private static bool ValueEquals(string? a, string? b, string type)
    {
        if (type.Equals("A", StringComparison.OrdinalIgnoreCase)
            || type.Equals("AAAA", StringComparison.OrdinalIgnoreCase))
        {
            return UnifiReservations.IpEquals(a, b);
        }

        return Normalize(a) == Normalize(b);
    }
}
