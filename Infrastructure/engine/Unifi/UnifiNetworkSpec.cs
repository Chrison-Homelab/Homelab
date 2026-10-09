namespace Homelab.Infrastructure.Unifi;

// Declarative UniFi network desired-state (homelab/v1, kind: UnifiNetwork) —
// the source for `converge-unifi`. Deserialized by YamlDotNet (camelCase,
// IgnoreUnmatchedProperties), same as the LXC/VM shapes. Plain classes with
// setters because YamlDotNet needs them.

public sealed class UnifiNetworkDoc
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public UnifiNetworkMetadata Metadata { get; set; } = new();
    public UnifiNetworkSpec Spec { get; set; } = new();
}

public sealed class UnifiNetworkMetadata
{
    public string Name { get; set; } = "";
}

public sealed class UnifiNetworkSpec
{
    public List<PortForwardSpec> PortForwards { get; set; } = [];

    /// <summary>
    /// Controller-local DNS records (#314/#419) — LAN-only resolution, no public zone
    /// involved. Declared here rather than on a guest shape because the useful ones are
    /// zone-level (a wildcard per Pangolin-fronted zone) and belong to no single member.
    /// Per-client records that ride a DHCP reservation are a different endpoint and live
    /// on the guest, as <c>network.reservation.localDnsRecord</c> (#416).
    /// </summary>
    public List<StaticDnsSpec> StaticDns { get; set; } = [];

    // Zone-based firewall policies (UnifiSharp#20). Reconciled by NAME, user-defined only:
    // a declared policy that is missing is created, one that differs is replaced (PUT), and
    // no undeclared or system-defined policy is ever touched.
    public List<FirewallPolicyDecl> FirewallPolicies { get; set; } = [];
}

/// <summary>network.yaml form of a firewall policy; <see cref="ToSpec"/> maps it onto UnifiSharp's model.</summary>
public sealed class FirewallPolicyDecl
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public string Action { get; set; } = "allow";            // allow | block | reject
    public bool AllowReturnTraffic { get; set; } = true;     // allow only
    public FirewallEndpointDecl Source { get; set; } = new();
    public FirewallEndpointDecl Destination { get; set; } = new();
    public string IpVersion { get; set; } = "both";          // ipv4 | ipv6 | both
    public string Protocol { get; set; } = "all";            // all | tcp_udp | tcp | udp | <named>
    public bool Logging { get; set; }

    public UnifiSharp.Firewall.FirewallPolicySpec ToSpec() => new()
    {
        Name = Name,
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Enabled = Enabled,
        Action = Action.ToLowerInvariant() switch
        {
            "allow" => UnifiSharp.Firewall.FirewallAction.Allow,
            "block" => UnifiSharp.Firewall.FirewallAction.Block,
            "reject" => UnifiSharp.Firewall.FirewallAction.Reject,
            var a => throw new InvalidOperationException($"firewall policy '{Name}': unknown action '{a}'"),
        },
        AllowReturnTraffic = Action.Equals("allow", StringComparison.OrdinalIgnoreCase) ? AllowReturnTraffic : true,
        Source = Source.ToEndpoint(),
        Destination = Destination.ToEndpoint(),
        IpVersion = IpVersion.ToLowerInvariant() switch
        {
            "ipv4" => UnifiSharp.Firewall.FirewallIpVersion.IPv4,
            "ipv6" => UnifiSharp.Firewall.FirewallIpVersion.IPv6,
            "both" => UnifiSharp.Firewall.FirewallIpVersion.Both,
            var v => throw new InvalidOperationException($"firewall policy '{Name}': unknown ipVersion '{v}'"),
        },
        Protocol = Protocol.ToLowerInvariant(),
        Logging = Logging,
    };
}

public sealed class FirewallEndpointDecl
{
    public string Zone { get; set; } = "";
    public List<string> Addresses { get; set; } = [];
    public List<string> Networks { get; set; } = [];
    public string? Ipv6InterfaceId { get; set; }
    public List<string> Ports { get; set; } = [];
    public bool MatchOpposite { get; set; }
    public bool MatchOppositePorts { get; set; }

    public UnifiSharp.Firewall.FirewallEndpoint ToEndpoint() => new()
    {
        Zone = Zone,
        Addresses = Addresses,
        Networks = Networks,
        Ipv6InterfaceId = string.IsNullOrWhiteSpace(Ipv6InterfaceId) ? null : Ipv6InterfaceId.Trim(),
        Ports = Ports,
        MatchOpposite = MatchOpposite,
        MatchOppositePorts = MatchOppositePorts,
    };
}

/// <summary>A WAN→LAN port-forward, declared in network.yaml. Maps to the legacy API's port-forward object.</summary>
public sealed class PortForwardSpec
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>The WAN the rule binds to: <c>wan</c>, <c>wan2</c>, or <c>both</c>.</summary>
    public string Interface { get; set; } = "wan";
    /// <summary>Permitted source — <c>any</c> or a CIDR/IP.</summary>
    public string Source { get; set; } = "any";
    public string DestinationPort { get; set; } = "";
    public string ForwardIp { get; set; } = "";
    public string ForwardPort { get; set; } = "";
    /// <summary><c>tcp</c>, <c>udp</c>, or <c>tcp_udp</c>.</summary>
    public string Protocol { get; set; } = "tcp";
    public bool Log { get; set; }
}
