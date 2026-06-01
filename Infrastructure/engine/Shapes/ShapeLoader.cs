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

    public sealed record LoadedStack(StackShape? Stack, IReadOnlyList<Shape> Members);

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
            members.Add(shape);
        }
        return new LoadedStack(stack, members);
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
        member.Network ??= defaults.Network;
        member.Features ??= defaults.Features;
        member.Timezone ??= defaults.Timezone;
        member.Console ??= defaults.Console;
        member.Pool ??= defaults.Pool;
        member.Hookscript ??= defaults.Hookscript;
        member.SshAuthorizedKey ??= defaults.SshAuthorizedKey;
        if (member.Networks.Count == 0 && defaults.Networks.Count > 0)
            member.Networks = defaults.Networks;
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
