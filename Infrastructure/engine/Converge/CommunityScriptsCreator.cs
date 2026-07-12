using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Creates an LXC from a shape via the community-scripts automated mode (the same
// `mode=generated var_* ... ct/<app>.sh` invocation as the BL-013 renderer), run
// over SSH. Idempotent: no-op if the CT already exists. This is the create half
// of converge's lifecycle (the app install needs community-scripts; ProxmoxSharp
// owns reads/discovery).
public sealed class CommunityScriptsCreator
{
    private readonly INodeExec _exec;
    public CommunityScriptsCreator(INodeExec exec) => _exec = exec;

    // Default stdin answer for the rare community-scripts installs that prompt with a raw
    // `read` and have no var_/unattended escape. The answer is streamed with `yes <ans> |`
    // (infinite), NOT a finite printf — the in-CT install reads run under lxc-attach and a
    // fixed number of newlines can under-supply (or mis-order) and BLOCK forever. `yes`
    // satisfies every prompt with the same answer regardless of count/order (#11).
    //   shelfmark → "1": captcha-bypass deployment type (its internal bypasser; switchable
    //               to the stack's FlareSolverr later via /etc/shelfmark/.env).
    //   docker    → "n": DECLINE every add-on prompt (Portainer UI, Portainer Agent, expose
    //               Docker TCP socket). docker-ce still includes the compose plugin.
    // Each app's prompts must all take the SAME answer for `yes` to be correct (true here).
    private static readonly Dictionary<string, string> InstallStdin = new(StringComparer.Ordinal)
    {
        ["shelfmark"] = "1",
        ["docker"] = "n",
    };

    public async Task<bool> ExistsAsync(string node, string ctid, CancellationToken ct) =>
        (await _exec.OnNodeAsync(node, $"pct status {ctid}", ct)).Ok;

    // Teardown half of the lifecycle (issue #101): stop (if running) then destroy
    // a CT we manage. Idempotent: a CT that's already absent is a no-op. Scoped to
    // a single CT by ctid — the ADD-ONLY guardrail (CLAUDE.md) means the caller
    // only ever passes ctids declared in our stack, never arbitrary cluster CTs.
    public async Task<ApplyResult> DestroyAsync(string node, string ctid, CancellationToken ct)
    {
        var status = await _exec.OnNodeAsync(node, $"pct status {ctid}", ct);
        if (!status.Ok) return ApplyResult.NoChange($"CT {ctid} absent");

        if (status.Stdout.Contains("running", StringComparison.OrdinalIgnoreCase))
        {
            var stop = await _exec.OnNodeAsync(node, $"pct stop {ctid}", ct);
            if (!stop.Ok) return ApplyResult.Failed($"stop failed: {stop.Stderr}");
        }

        var res = await _exec.OnNodeAsync(node, $"pct destroy {ctid}", ct);
        return res.Ok
            ? ApplyResult.Applied($"destroyed CT {ctid}")
            : ApplyResult.Failed($"destroy failed: {res.Stderr}");
    }

    public async Task<ApplyResult> EnsureAsync(Shape s, IReadOnlyDictionary<string, string>? extraVars, CancellationToken ct)
    {
        var sp = s.Spec;
        if (sp.Node is not { } node || sp.Ctid is not { } ctid || sp.App is not { } app)
            return ApplyResult.Failed("missing node/ctid/app");

        if (await ExistsAsync(node, ctid, ct)) return ApplyResult.NoChange($"CT {ctid} exists");

        var vars = BuildVars(s, extraVars);
        var channel = sp.Source?.Channel ?? "stable";
        var repo = sp.Source?.Repo ?? (channel == "dev" ? "community-scripts/ProxmoxVED" : "community-scripts/ProxmoxVE");
        var gitref = sp.Source?.Ref ?? "main";
        var url = $"https://raw.githubusercontent.com/{repo}/{gitref}/ct/{app}.sh";
        var cmd = $"TERM=xterm mode=generated {vars} bash -c \"$(curl -fsSL {url})\"";

        // A few community-scripts installs prompt with a raw `read` (some run inside the CT
        // via lxc-attach, where a finite stdin can under-supply and hang). Stream the default
        // answer infinitely with `yes` so every prompt is satisfied regardless of count/order.
        if (InstallStdin.TryGetValue(app, out var answer))
            cmd = $"yes '{answer}' | {cmd}";

        var res = await _exec.OnNodeAsync(node, cmd, ct);
        return res.Ok ? ApplyResult.Applied($"created CT {ctid} ({app}, {channel})")
                      : ApplyResult.Failed($"create failed: {res.Stderr}");
    }

    private static string BuildVars(Shape s, IReadOnlyDictionary<string, string>? extra)
    {
        var sp = s.Spec;
        var v = new List<string>();
        void Add(string k, string? val) { if (!string.IsNullOrEmpty(val)) v.Add($"{k}={Quote(val!)}"); }

        Add("var_hostname", s.Metadata.Name);
        Add("var_ctid", sp.Ctid);
        Add("var_cpu", sp.Cores?.ToString());
        Add("var_ram", sp.Memory?.ToString());
        Add("var_disk", sp.Disk?.ToString());
        Add("var_unprivileged", (sp.Unprivileged ?? true) ? "1" : "0");
        Add("var_os", sp.Os);
        Add("var_version", sp.OsVersion);
        Add("var_container_storage", sp.Storage);
        Add("var_template_storage", sp.TemplateStorage);
        if (sp.Network is { } n)
        {
            Add("var_brg", n.Bridge);
            Add("var_vlan", n.Vlan?.ToString());
            Add("var_net", n.Ipv4);
            Add("var_gateway", n.Gateway);   // static IPs need an explicit default route (else no internet egress)
            Add("var_ipv6_method", n.Ipv6);
            Add("var_mtu", n.Mtu?.ToString());
        }
        Add("var_ns", sp.Nameserver);
        Add("var_searchdomain", sp.Searchdomain);
        if (sp.Features is { } f)
        {
            if (f.Nesting is { } nest) Add("var_nesting", nest ? "1" : "0");
            if (f.Keyctl is { } kc) Add("var_keyctl", kc ? "1" : "0");   // Docker-in-unprivileged-LXC needs keyctl=1
            if (f.Fuse is { } fuse) Add("var_fuse", fuse ? "1" : "0");
        }
        var tags = sp.Tags.Concat(s.Metadata.Tags).Distinct().ToList();
        if (tags.Count > 0) Add("var_tags", string.Join(';', tags));

        if (extra is not null) foreach (var kv in extra) Add(kv.Key, kv.Value);

        return string.Join(' ', v);
    }

    private static string Quote(string val) =>
        val.IndexOfAny(new[] { ';', ' ', '"', '$' }) >= 0 ? $"'{val}'" : val;
}
