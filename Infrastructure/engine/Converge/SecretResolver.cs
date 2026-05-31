using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

public sealed record ResolvedSecret(string Name, SecretKind Kind, bool Ready, string Description);

// Resolves a shape's secrets WITHOUT exposing values. In plan mode it reports
// how each secret would be obtained and whether the inputs are available;
// applying the derivation (service/provider) is the provisioner's job (TODO).
public sealed class SecretResolver
{
    private readonly SecretsEnv _env;
    public SecretResolver(SecretsEnv env) => _env = env;

    public IReadOnlyList<ResolvedSecret> Plan(LxcSpec spec)
    {
        var result = new List<ResolvedSecret>();
        foreach (var s in spec.Secrets)
        {
            var vf = s.ValueFrom;
            switch (vf.Kind)
            {
                case SecretKind.Env:
                    var ready = _env.Has(vf.Env!);
                    result.Add(new(s.Name, SecretKind.Env, ready,
                        $"from secrets.env ${vf.Env} ({(ready ? "present" : "MISSING")})"));
                    break;
                case SecretKind.Service:
                    var svc = vf.Service!;
                    result.Add(new(s.Name, SecretKind.Service, true,
                        $"derived from service '{svc.Ref}' action '{svc.Action}'"));
                    break;
                case SecretKind.Provider:
                    var p = vf.Provider!;
                    var authReady = p.Auth?.Env is { } e ? _env.Has(e) : true;
                    var auth = p.Auth?.Env is { } ev ? $", auth ${ev} ({(_env.Has(ev) ? "present" : "MISSING")})" : "";
                    result.Add(new(s.Name, SecretKind.Provider, authReady,
                        $"derived from provider '{p.Name}' action '{p.Action}'{auth}"));
                    break;
                default:
                    result.Add(new(s.Name, SecretKind.None, false, "no valueFrom (invalid)"));
                    break;
            }
        }
        return result;
    }
}
