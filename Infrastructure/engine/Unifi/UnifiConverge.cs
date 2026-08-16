using UnifiSharp.Legacy;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Homelab.Infrastructure.Unifi;

// Reconciles the UnifiNetwork desired-state (network.yaml) against the live
// controller via UnifiSharp's legacy write adapter. ADD-ONLY (CLAUDE.md): create
// declared resources that are missing (matched by name); never delete what we
// didn't declare. The planner is pure (testable); apply does the I/O.
public static class UnifiConverge
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static UnifiNetworkDoc Load(string path) =>
        Yaml.Deserialize<UnifiNetworkDoc>(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"empty or invalid UnifiNetwork document: {path}");

    /// <summary>
    /// Add-only plan, matched by name (case-insensitive): a declared port-forward that
    /// is missing gets created, one that has drifted from the shape gets corrected, and
    /// anything we did not declare is left alone.
    /// <para>Correcting drift is the point. This used to stop at "is a rule with that
    /// name present?", so re-pointing the forward target in the UI left the shape and
    /// the controller disagreeing while converge reported success forever — the same
    /// write-once failure the Cloudflare Access bypass had (#417).</para>
    /// </summary>
    public static UnifiConvergePlan Plan(IEnumerable<PortForwardSpec> declared, IEnumerable<UnifiPortForward> existing)
    {
        var live = existing.ToList();
        var toCreate = new List<PortForwardSpec>();
        var toUpdate = new List<PortForwardDrift>();
        var present = new List<string>();

        foreach (var pf in declared)
        {
            var match = live.FirstOrDefault(
                p => string.Equals(p.Name, pf.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                toCreate.Add(pf);
                continue;
            }

            var changes = Drift(match, pf);
            if (changes.Count == 0)
            {
                present.Add(pf.Name);
            }
            else
            {
                toUpdate.Add(new PortForwardDrift(pf, match.Id ?? "", changes));
            }
        }

        return new UnifiConvergePlan(toCreate, present, toUpdate);
    }

    /// <summary>Field-by-field drift between a live port-forward and the shape.</summary>
    public static IReadOnlyList<string> Drift(UnifiPortForward live, PortForwardSpec desired)
    {
        var changes = new List<string>();
        Compare("enabled", live.Enabled?.ToString(), desired.Enabled.ToString());
        Compare("pfwd_interface", live.PfwdInterface, desired.Interface);
        Compare("src", live.Src, desired.Source);
        Compare("dst_port", live.DstPort, desired.DestinationPort);
        Compare("fwd", live.Fwd, desired.ForwardIp);
        Compare("fwd_port", live.FwdPort, desired.ForwardPort);
        Compare("proto", live.Proto, desired.Protocol);
        Compare("log", live.Log?.ToString(), desired.Log.ToString());
        return changes;

        void Compare(string field, string? liveValue, string? desiredValue)
        {
            // An undeclared field is not a claimed field — leave whatever is there.
            if (string.IsNullOrWhiteSpace(desiredValue)) return;
            if (string.Equals(liveValue?.Trim(), desiredValue.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            changes.Add($"{field}: {(string.IsNullOrWhiteSpace(liveValue) ? "(unset)" : liveValue)} → {desiredValue}");
        }
    }

    /// <summary>Map the declared spec to the legacy API's port-forward object (ports are strings there).</summary>
    public static UnifiPortForward ToLegacy(PortForwardSpec s) => new()
    {
        Name = s.Name,
        Enabled = s.Enabled,
        PfwdInterface = s.Interface,
        Src = s.Source,
        DstPort = s.DestinationPort,
        Fwd = s.ForwardIp,
        FwdPort = s.ForwardPort,
        Proto = s.Protocol,
        Log = s.Log,
    };

    /// <summary>
    /// Reconcile the doc against the controller. <paramref name="apply"/> false = dry-run
    /// (plan only, no writes). Returns a human-readable summary; throws on API failure.
    /// </summary>
#pragma warning disable CS0618 // UnifiLegacyClient is intentionally obsolete (ADR-0003)
    public static async Task<UnifiConvergeResult> ReconcileAsync(
        UnifiNetworkDoc doc, UnifiLegacyClient client, bool apply, CancellationToken ct = default)
    {
        var existing = await client.ListPortForwardsAsync(ct).ConfigureAwait(false);
        var plan = Plan(doc.Spec.PortForwards, existing);

        var created = new List<string>();
        var updated = new List<string>();
        if (apply)
        {
            foreach (var spec in plan.ToCreate)
            {
                await client.CreatePortForwardAsync(ToLegacy(spec), ct).ConfigureAwait(false);
                created.Add(spec.Name);
            }

            foreach (var drift in plan.ToUpdate)
            {
                await client.UpdatePortForwardAsync(drift.Id, ToLegacy(drift.Spec), ct).ConfigureAwait(false);
                updated.Add(drift.Spec.Name);
            }
        }

        // Static DNS (v2 site API). Read live even on a dry-run so the plan shows real
        // drift rather than intent.
        var liveDns = await client.ListStaticDnsAsync(ct).ConfigureAwait(false);
        var dnsPlan = doc.Spec.StaticDns.Select(d => UnifiStaticDns.Plan(d, liveDns)).ToList();
        if (apply)
        {
            foreach (var item in dnsPlan)
            {
                if (item.Action == StaticDnsAction.Create)
                {
                    await client.CreateStaticDnsAsync(UnifiStaticDns.ToRecord(item.Desired), ct).ConfigureAwait(false);
                }
                else if (item.Action == StaticDnsAction.Update)
                {
                    // Full replacement — v2 rejects partials — carrying the live record's
                    // untouched fields forward rather than resetting them to defaults.
                    var live = liveDns.First(r => r.Id == item.LiveId);
                    await client.UpdateStaticDnsAsync(item.LiveId!, UnifiStaticDns.ToRecord(item.Desired, live), ct)
                        .ConfigureAwait(false);
                }
            }
        }

        return new UnifiConvergeResult(plan, created, Applied: apply, Updated: updated,
            StaticDns: dnsPlan,
            UndeclaredDns: UnifiStaticDns.Undeclared(liveDns, doc.Spec.StaticDns));
    }
#pragma warning restore CS0618
}

/// <summary>A declared port-forward that exists but no longer matches the shape.</summary>
public sealed record PortForwardDrift(PortForwardSpec Spec, string Id, IReadOnlyList<string> Changes);

/// <summary>The add-only plan: which declared port-forwards to create, correct, or leave alone.</summary>
public sealed record UnifiConvergePlan(
    IReadOnlyList<PortForwardSpec> ToCreate,
    IReadOnlyList<string> AlreadyPresent,
    IReadOnlyList<PortForwardDrift> ToUpdate);

/// <summary>Outcome of a reconcile. <see cref="Created"/>/<see cref="Updated"/> are empty on a dry-run.</summary>
public sealed record UnifiConvergeResult(
    UnifiConvergePlan Plan,
    IReadOnlyList<string> Created,
    bool Applied,
    IReadOnlyList<string> Updated,
    IReadOnlyList<StaticDnsPlanItem> StaticDns,
    IReadOnlyList<UndeclaredDnsRecord> UndeclaredDns);
