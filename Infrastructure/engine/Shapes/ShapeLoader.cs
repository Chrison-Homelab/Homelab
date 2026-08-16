using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Homelab.Infrastructure.Shapes;

// Loads a stack directory: stack.yaml (kind: Stack) + *.lxc.yaml members,
// merging the stack's spec.defaults under each member (member wins) — mirrors
// the BL-013 PowerShell renderer.
public sealed class ShapeLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()   // model only the converge-relevant subset
        .Build();

    public sealed record LoadedStack(StackShape? Stack, IReadOnlyList<Shape> Members, IReadOnlyList<VmShape> VmMembers);

    public static LoadedStack LoadStack(string stackDir)
    {
        if (!Directory.Exists(stackDir))
            throw new DirectoryNotFoundException($"Stack directory not found: {stackDir}");

        StackShape? stack = null;
        var stackPath = Path.Combine(stackDir, "stack.yaml");
        if (File.Exists(stackPath))
            stack = Yaml.Deserialize<StackShape>(File.ReadAllText(stackPath));

        var members = new List<Shape>();
        foreach (var file in Directory.EnumerateFiles(stackDir, "*.lxc.yaml").OrderBy(f => f))
        {
            // Schema validation on the load path is advisory (WARNING) so a shape
            // that doesn't satisfy the canonical contract can't silently change
            // converge/discover behaviour — the strict gate is the `validate`
            // command (#43). Never fatal here.
            WarnIfInvalid(file);

            var shape = Yaml.Deserialize<Shape>(File.ReadAllText(file));
            if (!string.Equals(shape.Kind, "LXC", StringComparison.Ordinal)) continue;
            if (stack?.Spec.Defaults is { } d) Merge(d, shape.Spec);
            // Remember where the shape came from, so provisioners can read sibling assets
            // (e.g. the podman path's quadlet files) rather than requiring them inline.
            shape.SourceDir = stackDir;
            members.Add(shape);
        }

        // kind: VM members (*.vm.yaml) — converged via ProxmoxSharp, not community-scripts.
        var vmMembers = new List<VmShape>();
        foreach (var file in Directory.EnumerateFiles(stackDir, "*.vm.yaml").OrderBy(f => f))
        {
            WarnIfInvalid(file);
            var shape = Yaml.Deserialize<VmShape>(File.ReadAllText(file));
            if (!string.Equals(shape.Kind, "VM", StringComparison.Ordinal)) continue;
            if (stack?.Spec.Defaults is { } d) MergeVm(d, shape.Spec);
            vmMembers.Add(shape);
        }

        return new LoadedStack(stack, members, vmMembers);
    }

    // VM members inherit only the stack defaults a VM actually has (node, tags) —
    // the stack's spec.defaults reuses the LXC shape, so the rest doesn't apply.
    private static void MergeVm(LxcSpec defaults, VmSpec member)
    {
        member.Node ??= defaults.Node;
        if (defaults.Tags.Count > 0)
            member.Tags = defaults.Tags.Concat(member.Tags).Distinct().ToList();
    }

    private static void WarnIfInvalid(string file)
    {
        try
        {
            var result = ShapeValidator.ValidateFile(file);
            if (result.Valid) return;
            Console.Error.WriteLine($"warning: shape '{file}' does not satisfy shape.schema.json:");
            foreach (var f in result.Failures)
                Console.Error.WriteLine(f.ToString());
        }
        catch (Exception ex)
        {
            // Validation itself must never break the load path.
            Console.Error.WriteLine($"warning: could not validate '{file}': {ex.Message}");
        }
    }

    // Fill the primary-NIC sugar FIELD BY FIELD rather than all-or-nothing.
    //
    // It used to be `member.Network ??= defaults.Network`, so a member that declared any
    // part of `network:` silently forfeited every field it didn't restate. That was
    // invisible while the only reason to declare the block was to override the whole
    // thing — but a member now has a reason to add exactly one key (`reservation:`), and
    // under the old rule that would have dropped its VLAN, address and gateway on the
    // floor and rendered a NIC on the wrong network.
    //
    // A no-op for a member that declares the full block, which is all three that exist.
    private static NetworkSpec? MergeNetwork(NetworkSpec? defaults, NetworkSpec? member)
    {
        if (defaults is null) return member;

        // A reservation is never inherited — a stack default would otherwise hand every
        // member the same fixed address. Copying rather than handing back the defaults
        // instance also stops one member's later edits leaking into its siblings.
        if (member is null)
        {
            return new NetworkSpec
            {
                Bridge = defaults.Bridge,
                Vlan = defaults.Vlan,
                Ipv4 = defaults.Ipv4,
                Gateway = defaults.Gateway,
                Ipv6 = defaults.Ipv6,
                Mtu = defaults.Mtu,
                Hwaddr = defaults.Hwaddr,
                Firewall = defaults.Firewall,
            };
        }

        member.Bridge ??= defaults.Bridge;
        member.Vlan ??= defaults.Vlan;
        member.Ipv4 ??= defaults.Ipv4;
        member.Gateway ??= defaults.Gateway;
        member.Ipv6 ??= defaults.Ipv6;
        member.Mtu ??= defaults.Mtu;
        member.Hwaddr ??= defaults.Hwaddr;
        member.Firewall ??= defaults.Firewall;
        return member;
    }

    // Fill member spec from stack defaults where the member left it unset.
    private static void Merge(LxcSpec defaults, LxcSpec member)
    {
        member.Node ??= defaults.Node;
        member.Hostname ??= defaults.Hostname;
        member.Arch ??= defaults.Arch;
        member.Cores ??= defaults.Cores;
        member.Cpulimit ??= defaults.Cpulimit;
        member.Cpuunits ??= defaults.Cpuunits;
        member.Memory ??= defaults.Memory;
        member.Swap ??= defaults.Swap;
        member.Disk ??= defaults.Disk;
        member.Os ??= defaults.Os;
        member.OsVersion ??= defaults.OsVersion;
        member.Unprivileged ??= defaults.Unprivileged;
        member.Protection ??= defaults.Protection;
        member.Onboot ??= defaults.Onboot;
        member.Startup ??= defaults.Startup;
        member.Storage ??= defaults.Storage;
        member.TemplateStorage ??= defaults.TemplateStorage;
        member.RootfsOptions ??= defaults.RootfsOptions;
        member.Nameserver ??= defaults.Nameserver;
        member.Searchdomain ??= defaults.Searchdomain;
        if (member.Networks.Count == 0 && defaults.Networks.Count > 0)
            member.Networks = defaults.Networks;
        // A member declaring the full multi-NIC list owns net0 as well, so it must NOT also
        // inherit the stack's single-NIC `network` sugar. The schema forbids both in one
        // FILE, but it can't see a merge — without this gate, a stack whose defaults set
        // `network` (as every stack here does) would silently describe net0 twice, with
        // create reading the sugar and reconcile rewriting net0 from the list (#383).
        if (member.Networks.Count == 0)
            member.Network = MergeNetwork(defaults.Network, member.Network);
        member.Features ??= defaults.Features;
        member.Timezone ??= defaults.Timezone;
        member.Console ??= defaults.Console;
        member.Pool ??= defaults.Pool;
        member.Hookscript ??= defaults.Hookscript;
        member.SshAuthorizedKey ??= defaults.SshAuthorizedKey;
        if (member.Mounts.Count == 0 && defaults.Mounts.Count > 0)
            member.Mounts = defaults.Mounts;
        if (member.Devices.Count == 0 && defaults.Devices.Count > 0)
            member.Devices = defaults.Devices;
        if (member.LxcRaw.Count == 0 && defaults.LxcRaw.Count > 0)
            member.LxcRaw = defaults.LxcRaw;
        if (defaults.Tags.Count > 0)
            member.Tags = defaults.Tags.Concat(member.Tags).Distinct().ToList();

        if (member.Source is null) member.Source = defaults.Source;
        else if (defaults.Source is not null)
        {
            // member channel/repo win; inherit ref if unset
            if (member.Source.Ref == "main" && defaults.Source.Ref != "main")
                member.Source.Ref = defaults.Source.Ref;
        }

        // config: stack defaults first, member overrides per key
        if (defaults.Config.Count > 0)
        {
            var merged = new Dictionary<string, object?>(defaults.Config);
            foreach (var kv in member.Config) merged[kv.Key] = kv.Value;
            member.Config = merged;
        }
        // dependsOn / secrets are member-only (not inherited).
    }
}
