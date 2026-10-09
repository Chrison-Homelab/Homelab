using UnifiSharp.Firewall;

namespace Homelab.Infrastructure.Unifi;

public enum FirewallPolicyAction { NoChange, Create, Update }

public sealed record FirewallPolicyPlanItem(
    FirewallPolicySpec Desired, FirewallPolicyAction Action, Guid? LiveId, IReadOnlyList<string> Changes);

public sealed record FirewallConvergeResult(
    IReadOnlyList<FirewallPolicyPlanItem> Items,
    IReadOnlyList<string> Undeclared,
    IReadOnlyList<string> Conflicts);

/// <summary>
/// Reconciles network.yaml's <c>firewallPolicies</c> through UnifiSharp's integration-API
/// firewall client (UnifiSharp#20). Same model as port-forwards and static DNS: matched by
/// NAME, create-or-correct what is declared, never touch what isn't. Only USER_DEFINED live
/// policies are candidates; a system-defined policy sharing a declared name is reported as a
/// conflict, never overwritten. The planner is pure; apply does the I/O.
/// </summary>
public static class UnifiFirewallConverge
{
    public static FirewallConvergeResult Plan(
        IReadOnlyList<FirewallPolicySpec> declared, IReadOnlyList<LiveFirewallPolicy> live, FirewallNames names)
    {
        var items = new List<FirewallPolicyPlanItem>();
        var conflicts = new List<string>();
        foreach (var d in declared)
        {
            var matches = live.Where(p => p.Name == d.Name).ToList();
            var user = matches.Where(p => p.IsUserDefined).ToList();
            if (matches.Count > user.Count)
            {
                conflicts.Add($"{d.Name}: a {matches.First(p => !p.IsUserDefined).Origin} policy has this name — left alone");
                continue;
            }
            if (user.Count > 1)
            {
                conflicts.Add($"{d.Name}: {user.Count} user policies share this name — resolve by hand first");
                continue;
            }
            if (user.Count == 0)
            {
                items.Add(new(d, FirewallPolicyAction.Create, null, []));
                continue;
            }

            var (current, why) = FirewallPolicyJson.FromJson(user[0].Raw, names);
            if (current is null)
                items.Add(new(d, FirewallPolicyAction.Update, user[0].Id, [$"live policy {why}; it will be replaced by the declared one"]));
            else if (current == d)
                items.Add(new(d, FirewallPolicyAction.NoChange, user[0].Id, []));
            else
                items.Add(new(d, FirewallPolicyAction.Update, user[0].Id, Diff(current, d)));
        }

        var declaredNames = declared.Select(d => d.Name).ToHashSet();
        var undeclared = live.Where(p => p.IsUserDefined && !declaredNames.Contains(p.Name)).Select(p => p.Name).Order().ToList();
        return new(items, undeclared, conflicts);
    }

    public static IReadOnlyList<string> Diff(FirewallPolicySpec live, FirewallPolicySpec want)
    {
        var c = new List<string>();
        void F<T>(string n, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) c.Add($"{n}: {a} → {b}"); }
        F("enabled", live.Enabled, want.Enabled);
        F("action", live.Action, want.Action);
        if (want.Action == FirewallAction.Allow) F("allowReturnTraffic", live.AllowReturnTraffic, want.AllowReturnTraffic);
        F("ipVersion", live.IpVersion, want.IpVersion);
        F("protocol", live.Protocol, want.Protocol);
        F("logging", live.Logging, want.Logging);
        F("description", live.Description ?? "", want.Description ?? "");
        Endpoint("source", live.Source, want.Source, c);
        Endpoint("destination", live.Destination, want.Destination, c);
        return c;
    }

    private static void Endpoint(string side, FirewallEndpoint a, FirewallEndpoint b, List<string> c)
    {
        static string L(IReadOnlyList<string> x) => x.Count == 0 ? "-" : string.Join(",", x);
        if (a.Zone != b.Zone) c.Add($"{side}.zone: {a.Zone} → {b.Zone}");
        if (!FirewallEndpointSetEq(a.Addresses, b.Addresses)) c.Add($"{side}.addresses: {L(a.Addresses)} → {L(b.Addresses)}");
        if (!FirewallEndpointSetEq(a.Networks, b.Networks)) c.Add($"{side}.networks: {L(a.Networks)} → {L(b.Networks)}");
        if (a.Ipv6InterfaceId != b.Ipv6InterfaceId) c.Add($"{side}.ipv6InterfaceId: {a.Ipv6InterfaceId ?? "-"} → {b.Ipv6InterfaceId ?? "-"}");
        if (!FirewallEndpointSetEq(a.Ports, b.Ports)) c.Add($"{side}.ports: {L(a.Ports)} → {L(b.Ports)}");
        if (a.MatchOpposite != b.MatchOpposite) c.Add($"{side}.matchOpposite: {a.MatchOpposite} → {b.MatchOpposite}");
        if (a.MatchOppositePorts != b.MatchOppositePorts) c.Add($"{side}.matchOppositePorts: {a.MatchOppositePorts} → {b.MatchOppositePorts}");
    }

    private static bool FirewallEndpointSetEq(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    public static async Task<FirewallConvergeResult> ReconcileAsync(
        IReadOnlyList<FirewallPolicyDecl> declared, UnifiFirewallClient client, bool apply, CancellationToken ct = default)
    {
        var specs = declared.Select(d => d.ToSpec()).ToList();
        var names = await client.GetNamesAsync(ct).ConfigureAwait(false);
        // Resolve every zone/network name up front so a typo fails the plan, not half an apply.
        foreach (var s in specs) FirewallPolicyJson.ToJson(s, names);
        var live = await client.ListPoliciesAsync(ct).ConfigureAwait(false);
        var result = Plan(specs, live, names);
        if (!apply) return result;
        foreach (var item in result.Items)
        {
            if (item.Action == FirewallPolicyAction.Create)
                await client.CreateAsync(item.Desired, names, ct).ConfigureAwait(false);
            else if (item.Action == FirewallPolicyAction.Update)
                await client.UpdateAsync(item.LiveId!.Value, item.Desired, names, ct).ConfigureAwait(false);
        }
        return result;
    }
}
