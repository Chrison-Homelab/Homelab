using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

public sealed class ShapeValidatorTests : IDisposable
{
    private readonly string _dir;

    public ShapeValidatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shape-validator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, string yaml)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void KnownGoodLxcShape_Validates()
    {
        var path = Write("good.lxc.yaml", """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: good
              tags: [homelab]
            spec:
              node: hpe-01
              app: docker
              ctid: 3001
              cores: 2
              memory: 2048
              disk: 16
              unprivileged: true
              network:
                vlan: 1010
                ipv4: dhcp
              mounts:
                - type: nfs
                  storage: ds1813-nfs-volume-3
                  target: /mnt/media
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.True(result.Valid, string.Join("\n", result.Failures.Select(f => f.ToString())));
    }

    [Fact]
    public void CtidAuto_IsAccepted()
    {
        var path = Write("auto.lxc.yaml", """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: auto
            spec:
              app: docker
              ctid: auto
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.True(result.Valid, string.Join("\n", result.Failures.Select(f => f.ToString())));
    }

    [Fact]
    public void BadShape_MissingRequiredAndBadApiVersion_Fails()
    {
        var path = Write("bad.lxc.yaml", """
            apiVersion: homelab/v2
            kind: LXC
            metadata:
              name: broken
            spec:
              cores: 2
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.False(result.Valid);

        var text = string.Join("\n", result.Failures.Select(f => f.ToString()));
        // apiVersion const violated
        Assert.Contains("/apiVersion", text);
        // app + ctid required on a deployable LXC
        Assert.Contains("/spec", text);
    }

    [Fact]
    public void UnknownField_IsRejected_AdditionalPropertiesFalse()
    {
        var path = Write("extra.lxc.yaml", """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: extra
            spec:
              app: docker
              ctid: 3002
              totallyNotAField: true
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.False(result.Valid);
        var text = string.Join("\n", result.Failures.Select(f => f.ToString()));
        Assert.Contains("totallyNotAField", text);
    }

    // Backward-compat guard: every committed shape MUST validate. If this fails,
    // do NOT edit the shapes to force green — revisit the schema/model change.
    [Theory]
    [MemberData(nameof(CommittedShapes))]
    public void CommittedShape_Validates(string path)
    {
        var result = ShapeValidator.ValidateFile(path);
        Assert.True(result.Valid, $"{path}\n" + string.Join("\n", result.Failures.Select(f => f.ToString())));
    }

    public static IEnumerable<object[]> CommittedShapes()
    {
        var root = FindInfrastructureRoot();
        if (root is null) yield break;
        foreach (var dir in new[] { "nodes", "examples" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.yaml"))
                yield return new object[] { f };
        }
    }

    private static string? FindInfrastructureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Infrastructure", "schema", "shape.schema.json");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "Infrastructure");
            dir = dir.Parent;
        }
        return null;
    }
}
