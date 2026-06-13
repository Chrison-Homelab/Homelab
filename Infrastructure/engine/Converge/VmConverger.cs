using Homelab.Infrastructure.Shapes;
using ProxmoxSharp.Vm;

namespace Homelab.Infrastructure.Converge;

// L4: converge kind: VM members through the ProxmoxSharp VM write path (#115).
// Maps the hub's VmSpec (YAML shape) onto ProxmoxSharp's native QemuVmSpec, then
// reuses its reconciler (dry-run diff) + writer (apply). The hub never hand-writes
// Proxmox config strings — that lives in ProxmoxSharp.
public static class VmConverger
{
    public sealed record VmApplyResult(VmPlan Plan, ApplyOutcome Outcome, string Message);

    // hub VmSpec → ProxmoxSharp QemuVmSpec. Pure + testable (no I/O).
    public static QemuVmSpec ToQemuSpec(VmShape shape)
    {
        var s = shape.Spec;
        if (string.IsNullOrEmpty(s.Node))
            throw new InvalidOperationException($"VM '{shape.Metadata.Name}' has no node.");
        if (!int.TryParse(s.Vmid, out var vmid))
            throw new InvalidOperationException(
                $"VM '{shape.Metadata.Name}' needs a numeric vmid (got '{s.Vmid}'); 'auto' is not supported on the converge path yet.");

        return new QemuVmSpec
        {
            Node = s.Node!,
            Vmid = vmid,
            Name = s.Name ?? shape.Metadata.Name,
            Machine = s.Machine,
            Bios = s.Bios,
            Cpu = s.Cpu,
            Cores = s.Cores,
            Sockets = s.Sockets,
            Memory = s.Memory,
            Numa = s.Numa,
            Ostype = s.Ostype,
            Agent = s.Agent,
            Onboot = s.Onboot,
            Protection = s.Protection,
            Scsihw = s.Scsihw,
            Vga = s.Vga,
            Disks = s.Disks.Select(d => new QemuDisk
            {
                Id = d.Id, Storage = d.Storage, Source = d.Source, Size = d.Size,
                Ssd = d.Ssd, Iothread = d.Iothread, Discard = d.Discard,
            }).ToList(),
            Efidisk = s.Efidisk is { } e
                ? new QemuEfiDisk { Storage = e.Storage, Source = e.Source, Efitype = e.Efitype, PreEnrolledKeys = e.PreEnrolledKeys }
                : null,
            Tpmstate = s.Tpmstate is { } t
                ? new QemuTpmState { Storage = t.Storage, Source = t.Source, Version = t.Version }
                : null,
            Cdrom = s.Cdrom is { } c
                ? new QemuCdrom { Storage = c.Storage, Iso = c.Iso, Source = c.Source }
                : null,
            // Single-NIC sugar `network` → net0. (Multi-NIC `networks` not mapped yet.)
            Nets = s.Network is { } n
                ? [new QemuNet { Id = "net0", Model = "virtio", Bridge = n.Bridge, Mac = n.Hwaddr, Tag = n.Vlan, Firewall = n.Firewall }]
                : [],
            HostPci = s.Hostpci.Select((h, i) => new QemuHostPci
            {
                Id = $"hostpci{i}", Mapping = h.Mapping, Host = h.Id,
                Pcie = h.Pcie, XVga = h.XVga, Rombar = h.Rombar, Romfile = h.Romfile, Mdev = h.Mdev,
            }).ToList(),
            BootOrder = s.Boot?.Order ?? [],
            Tags = s.Tags,
        };
    }

    // Read-only diff against live state.
    public static async Task<VmPlan> PlanAsync(QemuWriter writer, VmShape shape, CancellationToken ct = default)
    {
        var desired = ToQemuSpec(shape);
        var live = await writer.GetConfigRawAsync(desired.Node, desired.Vmid, ct);
        return VmReconciler.Reconcile(desired, live);
    }

    // Apply: create the VM if absent, else set only the changed config keys. Idempotent.
    public static async Task<VmApplyResult> ApplyAsync(QemuWriter writer, VmShape shape, CancellationToken ct = default)
    {
        var desired = ToQemuSpec(shape);
        var live = await writer.GetConfigRawAsync(desired.Node, desired.Vmid, ct);
        var plan = VmReconciler.Reconcile(desired, live);

        switch (plan.Kind)
        {
            case VmActionKind.Skip:
                return new VmApplyResult(plan, ApplyOutcome.NoChange, "VM matches desired state");

            case VmActionKind.Create:
            {
                var upid = await writer.CreateAsync(desired, ct);
                if (upid is not null) await writer.WaitForTaskAsync(desired.Node, upid, ct: ct);
                return new VmApplyResult(plan, ApplyOutcome.Applied, $"created VM {desired.Vmid} ({plan.Changes.Count} param(s))");
            }

            default: // SetConfig
            {
                var changes = plan.Changes.ToDictionary(c => c.Key, c => c.To, StringComparer.Ordinal);
                var upid = await writer.SetConfigAsync(desired.Node, desired.Vmid, changes, ct);
                if (upid is not null) await writer.WaitForTaskAsync(desired.Node, upid, ct: ct);
                return new VmApplyResult(plan, ApplyOutcome.Applied,
                    $"set {changes.Count} key(s): {string.Join(", ", changes.Keys)}");
            }
        }
    }
}
