using System.Globalization;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Renders and compares Proxmox LXC `netN` interface strings (#383) so a member can
// declare more than one NIC via spec.networks[] and have converge build it.
//
// LXC netN is NOT QEMU netN. It is a comma-separated k=v list that needs `name=`
// (the interface name INSIDE the container) and `type=veth`, and it carries the MAC
// in its own `hwaddr=` key rather than on the model key (`virtio=AA:BB:...`). That
// difference is why this can't reuse the VM path.
public static class LxcNet
{
    // Render a shape interface → the value Proxmox expects for `--netN`.
    //
    // liveHwaddr preserves an EXISTING generated MAC when the shape doesn't pin one.
    // This matters more than it looks: re-issuing `pct set --netN` without hwaddr makes
    // Proxmox generate a NEW MAC, which silently invalidates the DHCP reservation the
    // address (and the UniFi DNS record keyed on it) depends on. So an unpinned NIC keeps
    // whatever MAC it was born with, and only a shape that names a hwaddr ever changes one.
    public static string Render(NetworkInterfaceSpec n, int index, string? liveHwaddr = null)
    {
        ArgumentNullException.ThrowIfNull(n);
        var parts = new List<string> { $"name={InterfaceName(n, index)}" };

        void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) parts.Add($"{k}={v}"); }

        Add("bridge", n.Bridge);
        Add("hwaddr", (n.Hwaddr ?? liveHwaddr)?.ToUpperInvariant());
        Add("ip", n.Ip);
        Add("gw", n.Gw);
        Add("ip6", n.Ip6);
        Add("gw6", n.Gw6);
        Add("tag", n.Tag?.ToString(CultureInfo.InvariantCulture));
        Add("trunks", n.Trunks);
        if (n.Firewall is { } fw) parts.Add($"firewall={(fw ? 1 : 0)}");
        Add("mtu", n.Mtu?.ToString(CultureInfo.InvariantCulture));
        Add("rate", n.Rate?.ToString(CultureInfo.InvariantCulture));
        if (n.LinkDown is { } down && down) parts.Add("link_down=1");
        parts.Add("type=veth");

        return string.Join(',', parts);
    }

    // Default interface name. A tagged NIC becomes `vlan<tag>` to match what the
    // community-scripts create path names net0 (see the `network` sugar in the schema) —
    // without that, reconcile would rename net0 on the first run and bounce the link for
    // no reason. Untagged falls back to eth<index>.
    public static string InterfaceName(NetworkInterfaceSpec n, int index)
    {
        ArgumentNullException.ThrowIfNull(n);
        if (!string.IsNullOrEmpty(n.Name)) return n.Name!;
        return n.Tag is { } tag ? $"vlan{tag}" : $"eth{index}";
    }

    // Parse a netN value into a key→value map. Proxmox prints the keys back in its own
    // order and adds ones we never set, so equality has to be per-key.
    public static Dictionary<string, string> Parse(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return map;
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            map[raw[..eq].Trim()] = raw[(eq + 1)..].Trim();
        }
        return map;
    }

    // Does the live interface already satisfy the desired one?
    //
    // Compares only the keys the shape declares — anything Proxmox added that we don't
    // manage is left alone, the same rule CtConfigReconciler applies everywhere else.
    // MACs compare case-insensitively because Proxmox echoes them uppercase regardless of
    // how they were authored.
    public static bool Matches(string? live, string desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (string.IsNullOrWhiteSpace(live)) return false;

        var l = Parse(live);
        foreach (var (key, want) in Parse(desired))
        {
            if (!l.TryGetValue(key, out var got)) return false;
            if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
