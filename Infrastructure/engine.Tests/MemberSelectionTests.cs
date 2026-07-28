using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// `--only <member>` selection (issue #306). Pure — no cluster, no filesystem.
public sealed class MemberSelectionTests
{
    private static Shape Lxc(string name, params string[] dependsOn)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = name } };
        s.Spec.DependsOn = dependsOn.ToList();
        return s;
    }

    private static VmShape Vm(string name) =>
        new() { Metadata = new ShapeMetadata { Name = name } };

    // ---- Parse ------------------------------------------------------------

    [Fact]
    public void Parse_AbsentFlag_ReturnsNull()
    {
        // null is NOT the same as an empty selection: null means "whole stack", which
        // is what every existing invocation relies on.
        Assert.Null(MemberSelection.Parse(new[] { "converge", "stacks/Media", "--apply" }));
    }

    [Theory]
    [InlineData("--only", "podman-host")]
    [InlineData("--only=podman-host", null)]
    public void Parse_AcceptsBothSpellings(string first, string? second)
    {
        var args = second is null
            ? new[] { "converge", "s", first }
            : new[] { "converge", "s", first, second };
        Assert.Equal(new[] { "podman-host" }, MemberSelection.Parse(args));
    }

    [Fact]
    public void Parse_SplitsCommasAndTrims()
    {
        var only = MemberSelection.Parse(new[] { "converge", "s", "--only", " prometheus , grafana " });
        Assert.Equal(new[] { "prometheus", "grafana" }, only);
    }

    [Fact]
    public void Parse_AccumulatesRepeatedFlags()
    {
        var only = MemberSelection.Parse(new[] { "converge", "s", "--only", "a", "--only", "b" });
        Assert.Equal(new[] { "a", "b" }, only);
    }

    [Fact]
    public void Parse_BareFlagBeforeAnotherFlagYieldsEmpty_NotTheFlagItself()
    {
        // `--only --apply` must not silently select a member literally named "--apply".
        var only = MemberSelection.Parse(new[] { "converge", "s", "--only", "--apply" });
        Assert.NotNull(only);
        Assert.Empty(only!);
    }

    // ---- Resolve ----------------------------------------------------------

    [Fact]
    public void Resolve_NullSelection_PassesEverythingThrough()
    {
        var all = new[] { Lxc("a"), Lxc("b") };
        var vms = new[] { Vm("v") };
        var (lxc, vm) = MemberSelection.Resolve(all, vms, null);
        Assert.Equal(2, lxc.Count);
        Assert.Single(vm);
    }

    [Fact]
    public void Resolve_KeepsOrderingOfTheFullStack()
    {
        // Selection must not reorder: `c` still comes after `a` even though the
        // --only list names them the other way round.
        var all = new[] { Lxc("a"), Lxc("b"), Lxc("c") };
        var (lxc, _) = MemberSelection.Resolve(all, Array.Empty<VmShape>(), new[] { "c", "a" });
        Assert.Equal(new[] { "a", "c" }, lxc.Select(s => s.Metadata.Name));
    }

    [Fact]
    public void Resolve_FiltersVmMembersToo()
    {
        // The adopted-member hazard in #306 was a VM (2000, Home Assistant), so the
        // filter is worthless if it only narrows the LXC side.
        var all = new[] { Lxc("podman-host") };
        var vms = new[] { Vm("homeassistant") };
        var (lxc, vm) = MemberSelection.Resolve(all, vms, new[] { "podman-host" });
        Assert.Single(lxc);
        Assert.Empty(vm);
    }

    [Fact]
    public void Resolve_UnknownName_Throws()
    {
        // The important case: a typo must NOT quietly converge nothing and exit 0.
        var all = new[] { Lxc("podman-host") };
        var ex = Assert.Throws<InvalidOperationException>(
            () => MemberSelection.Resolve(all, Array.Empty<VmShape>(), new[] { "podman-hots" }));
        Assert.Contains("podman-hots", ex.Message);
        Assert.Contains("podman-host", ex.Message); // lists what IS available
    }

    [Fact]
    public void Resolve_UnknownName_ThrowsEvenWhenAnotherNameIsValid()
    {
        var all = new[] { Lxc("a"), Lxc("b") };
        Assert.Throws<InvalidOperationException>(
            () => MemberSelection.Resolve(all, Array.Empty<VmShape>(), new[] { "a", "nope" }));
    }

    [Fact]
    public void Resolve_EmptySelection_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => MemberSelection.Resolve(new[] { Lxc("a") }, Array.Empty<VmShape>(), Array.Empty<string>()));
    }

    [Fact]
    public void Resolve_MatchIsCaseSensitive()
    {
        // Member names are used as dictionary keys with StringComparer.Ordinal
        // throughout converge; the filter must not be laxer than the thing it feeds.
        Assert.Throws<InvalidOperationException>(
            () => MemberSelection.Resolve(new[] { Lxc("podman-host") }, Array.Empty<VmShape>(), new[] { "Podman-Host" }));
    }

    // ---- UnselectedDependencies -------------------------------------------

    [Fact]
    public void UnselectedDependencies_ReportsDepsOutsideTheSelection()
    {
        var mate = Lxc("mate", "mqtt");
        var (lxc, _) = MemberSelection.Resolve(new[] { Lxc("mqtt"), mate }, Array.Empty<VmShape>(), new[] { "mate" });
        Assert.Equal(new[] { "mqtt" }, MemberSelection.UnselectedDependencies(lxc));
    }

    [Fact]
    public void UnselectedDependencies_IgnoresDepsInsideTheSelection()
    {
        var mqtt = Lxc("mqtt");
        var mate = Lxc("mate", "mqtt");
        var (lxc, _) = MemberSelection.Resolve(new[] { mqtt, mate }, Array.Empty<VmShape>(), new[] { "mqtt", "mate" });
        Assert.Empty(MemberSelection.UnselectedDependencies(lxc));
    }

    [Fact]
    public void UnselectedDependencies_DeduplicatesSharedDependencies()
    {
        var a = Lxc("a", "base");
        var b = Lxc("b", "base");
        var (lxc, _) = MemberSelection.Resolve(new[] { Lxc("base"), a, b }, Array.Empty<VmShape>(), new[] { "a", "b" });
        Assert.Equal(new[] { "base" }, MemberSelection.UnselectedDependencies(lxc));
    }

    [Fact]
    public void UnselectedDependencies_EmptyWhenNoSelectionApplied()
    {
        var all = new[] { Lxc("mqtt"), Lxc("mate", "mqtt") };
        var (lxc, _) = MemberSelection.Resolve(all, Array.Empty<VmShape>(), null);
        Assert.Empty(MemberSelection.UnselectedDependencies(lxc));
    }
}
