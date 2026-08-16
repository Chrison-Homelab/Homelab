using YamlDotNet.Serialization;

namespace Homelab.Infrastructure.Shapes;

// homelab/v1 shape model — the subset the converge engine needs (BL-010).
// Unknown schema fields are ignored by the loader, so this intentionally models
// only identity + the post-create contract (dependsOn / config / secrets).

public sealed class Shape
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public ShapeMetadata Metadata { get; set; } = new();
    public LxcSpec Spec { get; set; } = new();

    // The stack directory this shape was loaded from — set by ShapeLoader, never authored
    // in YAML. Lets a provisioner read sibling assets that live next to the shape rather
    // than being embedded in it (the podman path reads the stack's quadlet files this way).
    [YamlIgnore]
    public string? SourceDir { get; set; }
}

public sealed class ShapeMetadata
{
    public string Name { get; set; } = "";
    public string? Stack { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? Description { get; set; }
}

public sealed class LxcSpec
{
    public string? Node { get; set; }
    public string? App { get; set; }
    // Post-create provisioner override. The CT is CREATED via `app` (ct/<app>.sh), but the
    // post-create provisioner is dispatched by `provisioner ?? app`. Lets a generic Docker
    // host (app: docker) carry a rich app provisioner (e.g. pangolin) — see ADR-0007 / #168.
    public string? Provisioner { get; set; }
    public SourceSpec? Source { get; set; }
    public string? Ctid { get; set; }            // int or the literal "auto"

    public string? Hostname { get; set; }
    public string? Arch { get; set; }

    public int? Cores { get; set; }
    public double? Cpulimit { get; set; }
    public int? Cpuunits { get; set; }
    public int? Memory { get; set; }
    public int? Swap { get; set; }
    public int? Disk { get; set; }

    // Create-relevant (community-scripts var_*) — mostly inherited from the stack:
    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public bool? Unprivileged { get; set; }
    public bool? Protection { get; set; }
    public bool? Onboot { get; set; }

    // Lifecycle stance (#325). "describe-only" marks a guest ADOPTED IN PLACE: the shape
    // documents it, converge reads it and resolves dependsOn against it, but never writes
    // to it. An adopted guest's live config can never fully match the shape, so without
    // this it reports as drift on every single run and every apply wants to "fix" it.
    // Prose in CLAUDE.md plus remembering --only is not a guarantee; this is.
    public string? Manage { get; set; }
    public StartupSpec? Startup { get; set; }
    public string? Storage { get; set; }
    public string? TemplateStorage { get; set; }
    public RootfsOptionsSpec? RootfsOptions { get; set; }
    public string? Nameserver { get; set; }
    public string? Searchdomain { get; set; }
    public NetworkSpec? Network { get; set; }
    public List<NetworkInterfaceSpec> Networks { get; set; } = new();
    public FeaturesSpec? Features { get; set; }
    public List<MountSpec> Mounts { get; set; } = new();
    public List<DeviceSpec> Devices { get; set; } = new();
    public string? Timezone { get; set; }
    public ConsoleSpec? Console { get; set; }
    public string? Pool { get; set; }
    public string? Hookscript { get; set; }
    public List<LxcRawEntry> LxcRaw { get; set; } = new();
    public string? SshAuthorizedKey { get; set; }
    public List<string> Tags { get; set; } = new();

    // Post-create contract (converge-only):
    public List<string> DependsOn { get; set; } = new();
    public Dictionary<string, object?> Config { get; set; } = new();
    public List<Secret> Secrets { get; set; } = new();
}

// Primary NIC sugar (spec.network) — the community-scripts create path.
public sealed class NetworkSpec
{
    public string? Bridge { get; set; }
    public int? Vlan { get; set; }
    public string? Ipv4 { get; set; }
    public string? Gateway { get; set; }
    public string? Ipv6 { get; set; }
    public int? Mtu { get; set; }
    public string? Hwaddr { get; set; }
    public bool? Firewall { get; set; }
    public ReservationSpec? Reservation { get; set; }
}

/// <summary>
/// The UniFi DHCP reservation for one interface (#416). The MAC is deliberately NOT
/// declared here — it is read off the live guest after create, so a shape never has
/// to predict one. The UniFi network is resolved from the interface's VLAN tag.
/// </summary>
public sealed class ReservationSpec
{
    public string? FixedIp { get; set; }
    public string? LocalDnsRecord { get; set; }
    /// <summary>Controller-facing label; defaults to "&lt;member&gt; (CT &lt;ctid&gt;)".</summary>
    public string? Name { get; set; }
    /// <summary>
    /// Why the reservation is held for a deliberately-stopped guest. Non-empty means
    /// converge reports it but never writes it, so a parked guest neither drifts nor
    /// gets resurrected.
    /// </summary>
    public string? Parked { get; set; }

    public bool IsParked => !string.IsNullOrWhiteSpace(Parked);
}

// A single full Proxmox netX interface (spec.networks[]) — converge-only.
public sealed class NetworkInterfaceSpec
{
    public string? Name { get; set; }
    public string? Bridge { get; set; }
    public int? Tag { get; set; }
    public string? Trunks { get; set; }
    public bool? Firewall { get; set; }
    public string? Ip { get; set; }
    public string? Gw { get; set; }
    public string? Ip6 { get; set; }
    public string? Gw6 { get; set; }
    public string? Hwaddr { get; set; }
    public int? Mtu { get; set; }
    public double? Rate { get; set; }
    public bool? LinkDown { get; set; }
    public ReservationSpec? Reservation { get; set; }
}

public sealed class FeaturesSpec
{
    public bool? Nesting { get; set; }
    public bool? Keyctl { get; set; }
    public bool? Fuse { get; set; }
    public bool? Mknod { get; set; }
    public List<string> Mount { get; set; } = new();
    public bool? ForceRwSys { get; set; }
}

// A storage mount (Proxmox mpX). Modeled for #52/BL-016; NOT applied here.
public sealed class MountSpec
{
    public string Type { get; set; } = "";   // nfs | bind | volume | device
    public string? Storage { get; set; }
    public string? Source { get; set; }
    public string? Target { get; set; }
    public string? Size { get; set; }
    public bool? Ro { get; set; }
    public bool? Backup { get; set; }
    public bool? Acl { get; set; }
    public bool? Shared { get; set; }
    public bool? Replicate { get; set; }
    public bool? Quota { get; set; }
    public string? Mountoptions { get; set; }
}

// Host device passthrough (Proxmox devX) — converge.
public sealed class DeviceSpec
{
    public string Path { get; set; } = "";
    public int? Uid { get; set; }
    public int? Gid { get; set; }
    public string? Mode { get; set; }
    public bool? DenyWrite { get; set; }
}

// Boot order/delays (Proxmox startup=order=,up=,down=).
public sealed class StartupSpec
{
    public int? Order { get; set; }
    public int? Up { get; set; }
    public int? Down { get; set; }
}

// Extra root-volume options (rootfs).
public sealed class RootfsOptionsSpec
{
    public bool? Ro { get; set; }
    public bool? Acl { get; set; }
    public bool? Quota { get; set; }
    public bool? Replicate { get; set; }
    public string? Mountoptions { get; set; }
}

// Console attachment (Proxmox console/cmode/tty).
public sealed class ConsoleSpec
{
    public bool? Enabled { get; set; }
    public string? Mode { get; set; }
    public int? Tty { get; set; }
}

// Escape hatch: raw lxc.* config lines emitted verbatim.
public sealed class LxcRawEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class SourceSpec
{
    public string Channel { get; set; } = "stable";
    public string? Repo { get; set; }
    public string Ref { get; set; } = "main";
}

public sealed class Secret
{
    public string Name { get; set; } = "";
    public SecretSource ValueFrom { get; set; } = new();
}

// Exactly one of Env / Service / Provider is set (enforced by the schema).
public sealed class SecretSource
{
    public string? Env { get; set; }
    public ServiceSource? Service { get; set; }
    public ProviderSource? Provider { get; set; }

    public SecretKind Kind =>
        Env is not null ? SecretKind.Env
        : Service is not null ? SecretKind.Service
        : Provider is not null ? SecretKind.Provider
        : SecretKind.None;
}

public enum SecretKind { None, Env, Service, Provider }

public sealed class ServiceSource
{
    public string Ref { get; set; } = "";
    public string Action { get; set; } = "";
    public Dictionary<string, object?> With { get; set; } = new();
}

public sealed class ProviderSource
{
    public string Name { get; set; } = "";
    public string Action { get; set; } = "";
    public Dictionary<string, object?> With { get; set; } = new();
    public SecretSource? Auth { get; set; }
}

// kind: Stack — owns the CTID range + inheritable defaults.
public sealed class StackShape
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public ShapeMetadata Metadata { get; set; } = new();
    public StackSpec Spec { get; set; } = new();
}

public sealed class StackSpec
{
    public CtidRange? CtidRange { get; set; }
    public LxcSpec Defaults { get; set; } = new();
}

public sealed class CtidRange
{
    public int Start { get; set; }
    public int End { get; set; }
}

// kind: VM document — wraps a VmSpec (parallel to Shape/StackShape). Loaded from
// *.vm.yaml members and converged via ProxmoxSharp (VmConverger), not community-scripts.
public sealed class VmShape
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public ShapeMetadata Metadata { get; set; } = new();
    public VmSpec Spec { get; set; } = new();
}

// kind: VM — a Proxmox QEMU/KVM VM, provisioned by the ProxmoxSharp VM write
// path (#115), NOT the community-scripts (LXC) create path. Mirrors $defs.vmSpec
// 1:1 (enforced by SchemaDriftTests). Reuses StartupSpec/NetworkSpec/
// NetworkInterfaceSpec/Secret from the LXC side.
public sealed class VmSpec
{
    public string? Node { get; set; }
    public string? Vmid { get; set; }              // int or the literal "auto"
    public string? Name { get; set; }

    public string? Machine { get; set; }
    public string? Bios { get; set; }              // seabios | ovmf
    public string? Cpu { get; set; }
    public int? Cores { get; set; }
    public int? Sockets { get; set; }
    public int? Memory { get; set; }
    public bool? Numa { get; set; }
    public string? Ostype { get; set; }
    public bool? Agent { get; set; }
    public bool? Onboot { get; set; }
    public bool? Protection { get; set; }
    public StartupSpec? Startup { get; set; }

    // Lifecycle stance (#325). "describe-only" marks a guest ADOPTED IN PLACE: the shape
    // documents it, converge reads it and resolves dependsOn against it, but never writes
    // to it. An adopted guest's live config can never fully match the shape, so without
    // this it reports as drift on every single run and every apply wants to "fix" it.
    // Prose in CLAUDE.md plus remembering --only is not a guarantee; this is.
    public string? Manage { get; set; }

    public string? Scsihw { get; set; }
    public string? Vga { get; set; }
    public List<VmDiskSpec> Disks { get; set; } = new();
    public VmEfiDiskSpec? Efidisk { get; set; }
    public VmTpmStateSpec? Tpmstate { get; set; }
    public VmCdromSpec? Cdrom { get; set; }

    public NetworkSpec? Network { get; set; }
    public List<NetworkInterfaceSpec> Networks { get; set; } = new();
    public List<HostPciSpec> Hostpci { get; set; } = new();
    public VmBootSpec? Boot { get; set; }

    public string? Pool { get; set; }
    public List<string> Tags { get; set; } = new();

    // Post-create contract (converge-only):
    public List<string> DependsOn { get; set; } = new();
    public Dictionary<string, object?> Config { get; set; } = new();
    public List<Secret> Secrets { get; set; } = new();
}

// A QEMU disk (Proxmox scsiN/virtioN/sataN).
public sealed class VmDiskSpec
{
    public string Id { get; set; } = "";          // e.g. "scsi0"
    public string? Storage { get; set; }
    public string? Source { get; set; }           // adopt an existing volume
    public int? Size { get; set; }                // GB (fresh allocation)
    public bool? Ssd { get; set; }
    public bool? Iothread { get; set; }
    public bool? Discard { get; set; }
    public string? Cache { get; set; }
    public bool? Backup { get; set; }
}

// UEFI vars disk (Proxmox efidisk0) — required for bios: ovmf.
public sealed class VmEfiDiskSpec
{
    public string? Storage { get; set; }
    public string? Source { get; set; }
    public string? Efitype { get; set; }          // 2m | 4m
    public bool? PreEnrolledKeys { get; set; }
}

// vTPM state disk (Proxmox tpmstate0).
public sealed class VmTpmStateSpec
{
    public string? Storage { get; set; }
    public string? Source { get; set; }
    public string? Version { get; set; }          // v1.2 | v2.0
}

// Install/boot ISO (Proxmox ideN media=cdrom).
public sealed class VmCdromSpec
{
    public string? Storage { get; set; }
    public string? Iso { get; set; }
    public string? Source { get; set; }
}

// PCI(e) passthrough device (Proxmox hostpciN) — the gaming shape's GPU line.
public sealed class HostPciSpec
{
    public string? Mapping { get; set; }           // PCI resource-mapping name (preferred; token-settable)
    public string? Id { get; set; }                // raw PCI address, e.g. "0000:09:00" (root@pam only)
    public bool? Pcie { get; set; }
    public bool? XVga { get; set; }
    public bool? Rombar { get; set; }
    public string? Romfile { get; set; }
    public string? Mdev { get; set; }
}

// Boot device order (Proxmox boot=order=...).
public sealed class VmBootSpec
{
    public List<string> Order { get; set; } = new();
}
