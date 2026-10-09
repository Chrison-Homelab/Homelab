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
    public void QuotedScalar_StaysAString_EvenWhenItLooksLikeANumber()
    {
        // Regression (#508): a bare `24.04` is a YAML float, so a validator that re-types
        // every scalar by its text made `osVersion: "24.04"` impossible to author — it
        // reported "should be string, integer" against a value the author HAD quoted, and
        // that ShapeLoader already reads as a string. Ubuntu is the first OS in the lab
        // whose version is not an integer, so nothing hit this until InvenTree.
        var path = Write("quoted.lxc.yaml", """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: quoted
            spec:
              node: hpe-01
              app: inventree
              ctid: 8000
              os: ubuntu
              osVersion: "24.04"
              network:
                ipv4: dhcp
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.True(result.Valid, string.Join("\n", result.Failures.Select(f => f.ToString())));
    }

    [Fact]
    public void PlainScalars_AreStillRetyped()
    {
        // The other half of the same fix: unquoted scalars must keep their implicit types,
        // or `ctid: 3001` and `unprivileged: true` stop satisfying integer/boolean.
        var json = ShapeValidator.ParseToJsonNode("a: 3001\nb: true\nc: 1.5\nd: plain\ne: \"3001\"\n")!;

        Assert.Equal(3001, json["a"]!.GetValue<long>());
        Assert.True(json["b"]!.GetValue<bool>());
        Assert.Equal(1.5, json["c"]!.GetValue<double>());
        Assert.Equal("plain", json["d"]!.GetValue<string>());
        Assert.Equal("3001", json["e"]!.GetValue<string>());
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
    public void KnownGoodVmShape_Validates()
    {
        var path = Write("good.vm.yaml", """
            apiVersion: homelab/v1
            kind: VM
            metadata:
              name: bazzite
              stack: Gaming
              tags: [gaming, passthrough]
            spec:
              node: desktop-01
              vmid: 1003
              machine: q35
              bios: ovmf
              cpu: host
              cores: 6
              memory: 12288
              ostype: l26
              agent: true
              scsihw: virtio-scsi-single
              disks:
                - id: scsi0
                  storage: local-lvm
                  source: vm-1003-disk-1
                  size: 120
                  ssd: true
                  iothread: true
              efidisk:
                storage: local-lvm
                source: vm-1003-disk-0
                efitype: 4m
                preEnrolledKeys: true
              network:
                bridge: vmbr0
              hostpci:
                - id: "0000:09:00"
                  pcie: true
                  xVga: true
              boot:
                order: [scsi0, ide2, net0]
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.True(result.Valid, string.Join("\n", result.Failures.Select(f => f.ToString())));
    }

    [Fact]
    public void VmShape_MissingVmid_Fails()
    {
        var path = Write("no-vmid.vm.yaml", """
            apiVersion: homelab/v1
            kind: VM
            metadata:
              name: bazzite
            spec:
              node: desktop-01
              cores: 6
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.False(result.Valid);
        var text = string.Join("\n", result.Failures.Select(f => f.ToString()));
        Assert.Contains("/spec", text);
    }

    [Fact]
    public void VmShape_UnknownField_IsRejected()
    {
        var path = Write("extra.vm.yaml", """
            apiVersion: homelab/v1
            kind: VM
            metadata:
              name: bazzite
            spec:
              vmid: 1003
              totallyNotAVmField: true
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.False(result.Valid);
        var text = string.Join("\n", result.Failures.Select(f => f.ToString()));
        Assert.Contains("totallyNotAVmField", text);
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

    [Fact]
    public void LxcDeclaringBothNetworkAndNetworks_IsRejected()
    {
        // #383 — they are two spellings of the same net0. Allowing both means create reads
        // one and reconcile rewrites net0 from the other, bouncing the link every converge.
        var path = Write("both-nets.lxc.yaml", """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: both-nets
            spec:
              node: hpe-01
              app: docker
              ctid: 6005
              network:
                bridge: vmbr0
                vlan: 1010
                ipv4: dhcp
              networks:
                - bridge: vmbr0
                  tag: 1010
                  ip: dhcp
                - bridge: vmbr0
                  tag: 1040
                  ip: dhcp
            """);

        Assert.False(ShapeValidator.ValidateFile(path).Valid);
    }

    [Fact]
    public void LxcDeclaringOnlyNetworks_Validates()
    {
        var path = Write("multi-nic.lxc.yaml", """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: multi-nic
            spec:
              node: hpe-01
              app: docker
              ctid: 6005
              networks:
                - bridge: vmbr0
                  tag: 1010
                  ip: dhcp
                  firewall: true
                - bridge: vmbr0
                  tag: 1040
                  ip: dhcp
                  firewall: true
            """);

        var result = ShapeValidator.ValidateFile(path);
        Assert.True(result.Valid, string.Join("\n", result.Failures.Select(f => f.ToString())));
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
