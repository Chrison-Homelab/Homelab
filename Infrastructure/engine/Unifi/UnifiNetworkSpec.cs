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
