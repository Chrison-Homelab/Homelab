using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using ProxmoxSharp.Vm;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// L4: the hub VmSpec → ProxmoxSharp QemuVmSpec mapper + *.vm.yaml loading.
public sealed class VmConvergerTests
{
    private static VmShape Bazzite() => new()
    {
        ApiVersion = "homelab/v1",
        Kind = "VM",
        Metadata = new ShapeMetadata { Name = "bazzite" },
        Spec = new VmSpec
        {
            Node = "desktop-01",
            Vmid = "1003",
            Name = "bazzite",
            Machine = "q35",
            Bios = "ovmf",
            Cpu = "host",
            Cores = 6,
            Memory = 12288,
            Ostype = "l26",
            Agent = true,
            Onboot = false,
            Scsihw = "virtio-scsi-single",
            Disks = { new VmDiskSpec { Id = "scsi0", Storage = "local-lvm", Source = "vm-1003-disk-1", Iothread = true, Ssd = true } },
            Efidisk = new VmEfiDiskSpec { Storage = "local-lvm", Source = "vm-1003-disk-0", Efitype = "4m", PreEnrolledKeys = true },
            Network = new NetworkSpec { Bridge = "vmbr0" },
            Hostpci = { new HostPciSpec { Mapping = "AMD_Radeon_RX6600", Pcie = true, XVga = true } },
            Boot = new VmBootSpec { Order = { "scsi0", "ide2", "net0" } },
            Tags = { "gaming" },
        },
    };

    [Fact]
    public void ToQemuSpec_maps_the_bazzite_shape()
    {
        var q = VmConverger.ToQemuSpec(Bazzite());

        Assert.Equal("desktop-01", q.Node);
        Assert.Equal(1003, q.Vmid);
        Assert.Equal("bazzite", q.Name);
        Assert.Equal("q35", q.Machine);
        Assert.Equal("ovmf", q.Bios);
        Assert.Equal(6, q.Cores);
        Assert.Equal(12288, q.Memory);
        Assert.False(q.Onboot);

        var disk = Assert.Single(q.Disks);
        Assert.Equal("scsi0", disk.Id);
        Assert.Equal("vm-1003-disk-1", disk.Source);
        Assert.True(disk.Iothread);

        Assert.Equal("4m", q.Efidisk!.Efitype);

        var net = Assert.Single(q.Nets);
        Assert.Equal("net0", net.Id);
        Assert.Equal("vmbr0", net.Bridge);

        var pci = Assert.Single(q.HostPci);
        Assert.Equal("hostpci0", pci.Id);                 // indexed by position
        Assert.Equal("AMD_Radeon_RX6600", pci.Mapping);
        Assert.True(pci.XVga);
        // round-trips to the token-settable mapping form
        Assert.Equal("mapping=AMD_Radeon_RX6600,pcie=1,x-vga=1", QemuParamEncoder.EncodeHostPci(pci));

        Assert.Equal(new[] { "scsi0", "ide2", "net0" }, q.BootOrder);
        Assert.Contains("gaming", q.Tags);
    }

    [Fact]
    public void ToQemuSpec_falls_back_to_metadata_name()
    {
        var s = Bazzite();
        s.Spec.Name = null;
        Assert.Equal("bazzite", VmConverger.ToQemuSpec(s).Name);
    }

    [Fact]
    public void ToQemuSpec_indexes_multiple_hostpci()
    {
        var s = Bazzite();
        s.Spec.Hostpci.Add(new HostPciSpec { Id = "0000:09:00.1", Pcie = true });
        var q = VmConverger.ToQemuSpec(s);
        Assert.Equal(new[] { "hostpci0", "hostpci1" }, q.HostPci.Select(h => h.Id).ToArray());
        Assert.Equal("0000:09:00.1", q.HostPci[1].Host);   // raw id form preserved
    }

    [Fact]
    public void ToQemuSpec_rejects_non_numeric_vmid()
    {
        var s = Bazzite();
        s.Spec.Vmid = "auto";
        Assert.Throws<InvalidOperationException>(() => VmConverger.ToQemuSpec(s));
    }

    [Fact]
    public void LoadStack_picks_up_vm_members_and_merges_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vm-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "stack.yaml"), """
                apiVersion: homelab/v1
                kind: Stack
                metadata: { name: Gaming }
                spec:
                  ctidRange: { start: 1000, end: 1099 }
                  defaults:
                    node: desktop-01
                    tags: [gaming]
                """);
            File.WriteAllText(Path.Combine(dir, "foo.vm.yaml"), """
                apiVersion: homelab/v1
                kind: VM
                metadata: { name: foo }
                spec:
                  vmid: 1010
                  hostpci:
                    - mapping: AMD_Radeon_RX6600
                      pcie: true
                """);

            var loaded = ShapeLoader.LoadStack(dir);

            var vm = Assert.Single(loaded.VmMembers);
            Assert.Equal("foo", vm.Metadata.Name);
            Assert.Equal("1010", vm.Spec.Vmid);
            Assert.Equal("desktop-01", vm.Spec.Node);     // inherited from stack defaults
            Assert.Contains("gaming", vm.Spec.Tags);      // inherited from stack defaults
            Assert.Empty(loaded.Members);                 // no LXC members
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static VmPlan SetConfigPlan(params PlannedChange[] changes) =>
        new("desktop-01", 1002, VmActionKind.SetConfig, changes, []);

    [Fact]
    public void RawToMapping_detects_raw_address_to_mapping_transition()
    {
        // The 1002 (Windows) adopt: live raw hostpci0 → the AMD_Radeon_RX6600 mapping.
        var plan = SetConfigPlan(
            new PlannedChange("hostpci0", "0000:09:00,pcie=1,x-vga=1", "mapping=AMD_Radeon_RX6600,pcie=1,x-vga=1"),
            new PlannedChange("onboot", null, "0"),
            new PlannedChange("tags", null, "gaming"));

        Assert.Equal(new[] { "hostpci0" }, VmConverger.RawToMappingTransitions(plan));
    }

    [Fact]
    public void RawToMapping_ignores_a_fresh_mapping_add()
    {
        // The 1003 (Bazzite) adopt: hostpci0 added fresh (no prior value) — a plain add,
        // NOT a transition, so it must NOT be dropped-then-set.
        var plan = SetConfigPlan(
            new PlannedChange("hostpci0", null, "mapping=AMD_Radeon_RX6600,pcie=1,x-vga=1"));

        Assert.Empty(VmConverger.RawToMappingTransitions(plan));
    }

    [Fact]
    public void RawToMapping_ignores_mapping_to_mapping_and_non_hostpci_changes()
    {
        var plan = SetConfigPlan(
            new PlannedChange("hostpci0", "mapping=Old_GPU,pcie=1", "mapping=AMD_Radeon_RX6600,pcie=1"),  // already mapped
            new PlannedChange("memory", "12288", "16384"));                                               // unrelated key
        Assert.Empty(VmConverger.RawToMappingTransitions(plan));
    }
}
