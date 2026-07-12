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
    /// Add-only plan: a declared port-forward whose name is already present is left
    /// alone; the rest are created. Names compared case-insensitively.
    /// </summary>
    public static UnifiConvergePlan Plan(IEnumerable<PortForwardSpec> declared, IEnumerable<string> existingNames)
    {
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var toCreate = new List<PortForwardSpec>();
        var present = new List<string>();
        foreach (var pf in declared)
        {
            if (existing.Contains(pf.Name)) present.Add(pf.Name);
            else toCreate.Add(pf);
        }
        return new UnifiConvergePlan(toCreate, present);
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
        var plan = Plan(doc.Spec.PortForwards, existing.Select(p => p.Name ?? ""));

        var created = new List<string>();
        if (apply)
        {
            foreach (var spec in plan.ToCreate)
            {
                await client.CreatePortForwardAsync(ToLegacy(spec), ct).ConfigureAwait(false);
                created.Add(spec.Name);
            }
        }

        return new UnifiConvergeResult(plan, created, Applied: apply);
    }
#pragma warning restore CS0618
}

/// <summary>The add-only plan: which declared port-forwards to create vs. already present.</summary>
public sealed record UnifiConvergePlan(IReadOnlyList<PortForwardSpec> ToCreate, IReadOnlyList<string> AlreadyPresent);

/// <summary>Outcome of a reconcile. <see cref="Created"/> is empty on a dry-run.</summary>
public sealed record UnifiConvergeResult(UnifiConvergePlan Plan, IReadOnlyList<string> Created, bool Applied);
