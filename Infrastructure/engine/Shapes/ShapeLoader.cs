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
            var shape = Yaml.Deserialize<Shape>(File.ReadAllText(file));
            if (!string.Equals(shape.Kind, "LXC", StringComparison.Ordinal)) continue;
            if (stack?.Spec.Defaults is { } d) Merge(d, shape.Spec);
            members.Add(shape);
        }
        return new LoadedStack(stack, members);
    }

    // Fill member spec from stack defaults where the member left it unset.
    private static void Merge(LxcSpec defaults, LxcSpec member)
    {
        member.Node ??= defaults.Node;
        member.Cores ??= defaults.Cores;
        member.Memory ??= defaults.Memory;
        member.Disk ??= defaults.Disk;

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
