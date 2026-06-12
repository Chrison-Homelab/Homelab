using System.Text.RegularExpressions;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Resolves a SecretSource to an actual value at apply time. Pre-existing secrets
// come from secrets.env; derived ones are minted by calling the service/provider.
// (Cloudflare tunnel-token is provisioner-driven — it requires tunnel creation —
// so it is handled inside CloudflaredProvisioner, not here.)
public sealed class SecretDeriver
{
    private readonly SecretsEnv _env;
    private readonly INodeExec _exec;
    private readonly IReadOnlyDictionary<string, Shape> _byName;

    public SecretDeriver(SecretsEnv env, INodeExec exec, IReadOnlyDictionary<string, Shape> byName)
    {
        _env = env;
        _exec = exec;
        _byName = byName;
    }

    public async Task<string> ResolveAsync(SecretSource src, CancellationToken ct = default) => src.Kind switch
    {
        SecretKind.Env => _env.Get(src.Env!) ?? throw new InvalidOperationException($"secrets.env missing ${src.Env}"),
        SecretKind.Service => await ServiceAsync(src.Service!, ct),
        SecretKind.Provider => await ProviderAsync(src.Provider!, ct),
        _ => throw new InvalidOperationException("secret has no valueFrom"),
    };

    private async Task<string> ServiceAsync(ServiceSource s, CancellationToken ct)
    {
        if (!_byName.TryGetValue(s.Ref, out var shape))
            throw new InvalidOperationException($"service '{s.Ref}' not in stack");
        var node = shape.Spec.Node ?? throw new InvalidOperationException($"service '{s.Ref}' has no node");
        var ctid = shape.Spec.Ctid ?? throw new InvalidOperationException($"service '{s.Ref}' has no ctid");

        return s.Action switch
        {
            "generate-runner-token" => Token(await _exec.InContainerAsync(node, ctid,
                "su - git -s /bin/bash -c \"forgejo --config /etc/forgejo/app.ini forgejo-cli actions generate-runner-token\"", ct)),
            _ => throw new InvalidOperationException($"unknown service action '{s.Action}' on '{s.Ref}'"),
        };
    }

    private async Task<string> ProviderAsync(ProviderSource p, CancellationToken ct)
    {
        var auth = p.Auth is not null ? await ResolveAsync(p.Auth, ct)
            : throw new InvalidOperationException($"provider '{p.Name}' needs auth");
        return (p.Name, p.Action) switch
        {
            ("github", "org-runner-token") => await new GithubApi(auth)
                .CreateOrgRunnerTokenAsync(p.With.Str("org") ?? throw new InvalidOperationException("github org-runner-token needs with.org"), ct),
            ("cloudflare", _) => throw new InvalidOperationException("cloudflare tokens are provisioner-driven (CloudflaredProvisioner)"),
            _ => throw new InvalidOperationException($"unknown provider action '{p.Name}/{p.Action}'"),
        };
    }

    private static string Token(ExecResult r)
    {
        if (!r.Ok) throw new InvalidOperationException($"derivation failed: {r.Stderr}");
        var m = Regex.Matches(r.Stdout, "[A-Za-z0-9_-]{30,}");
        return m.Count > 0 ? m[^1].Value : throw new InvalidOperationException("no token in output");
    }
}
