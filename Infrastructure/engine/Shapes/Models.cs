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
}

public sealed class ShapeMetadata
{
    public string Name { get; set; } = "";
    public string? Stack { get; set; }
    public List<string> Tags { get; set; } = new();
}

public sealed class LxcSpec
{
    public string? Node { get; set; }
    public string? App { get; set; }
    public SourceSpec? Source { get; set; }
    public string? Ctid { get; set; }            // int or the literal "auto"
    public int? Cores { get; set; }
    public int? Memory { get; set; }
    public int? Disk { get; set; }

    // Create-relevant (community-scripts var_*) — mostly inherited from the stack:
    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public bool? Unprivileged { get; set; }
    public string? Storage { get; set; }
    public string? TemplateStorage { get; set; }
    public string? Nameserver { get; set; }
    public string? Searchdomain { get; set; }
    public NetworkSpec? Network { get; set; }
    public FeaturesSpec? Features { get; set; }
    public List<string> Tags { get; set; } = new();

    // Post-create contract (converge-only):
    public List<string> DependsOn { get; set; } = new();
    public Dictionary<string, object?> Config { get; set; } = new();
    public List<Secret> Secrets { get; set; } = new();
}

public sealed class NetworkSpec
{
    public string? Bridge { get; set; }
    public int? Vlan { get; set; }
    public string? Ipv4 { get; set; }
    public string? Ipv6 { get; set; }
    public int? Mtu { get; set; }
}

public sealed class FeaturesSpec
{
    public bool? Nesting { get; set; }
    public bool? Fuse { get; set; }
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
