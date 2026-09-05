using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Dashboard;

// The homelab dashboard, rendered from the shapes (ADR-0012, #47).
//
// INPUT is every stack's declarations: each shape's `metadata.services` (what a guest offers
// a human), the Pangolin resources (what is publicly exposed, and whether SSO gates it) and the
// cloudflared ingress rules (the other public path). OUTPUT is Homepage's services.yaml.
//
// The dashboard therefore cannot drift: nothing here is a second, hand-kept list. A service
// appears when its shape declares it; its public URL and gate come from the SAME resource
// declaration that converge applies to Pangolin, matched by name.
//
// TWO THINGS ARE DELIBERATELY LOUD rather than plausible:
//   * A Pangolin resource with no matching service is still rendered — under its own group,
//     linking to its public URL — and reported by Check(). A publicly exposed UI that is
//     missing from the dashboard is the exact drift this exists to prevent, so it is shown
//     as a gap, never omitted.
//   * A widget that names a secret the dashboard host does not export is reported by Check().
//     Homepage would otherwise render the literal "{{HOMEPAGE_VAR_X}}" and fail every call.
public static class HomepageDashboard
{
    public sealed record Entry(
        string Group, string Name, string Href, string? Icon, string Description,
        string? SiteMonitor, IReadOnlyDictionary<string, object?>? Widget);

    // One public hostname, how it is gated, and every name it can be matched under
    // (a Pangolin resource contributes its display name AND its subdomain).
    public sealed record PublicRoute(string Host, string Gate, IReadOnlySet<string> Keys);

    public sealed record Model(
        IReadOnlyList<Entry> Entries,
        IReadOnlyList<PublicRoute> UnassignedRoutes,   // exposed publicly, declared by no service
        IReadOnlySet<string> SecretRefs);              // every HOMEPAGE_VAR_<NAME> the render uses

    public const string UnassignedGroup = "Unassigned — declare metadata.services";

    // ── build ────────────────────────────────────────────────────────────────────

    public static Model Build(IEnumerable<(string StackName, ShapeLoader.LoadedStack Stack)> stacks)
    {
        var all = stacks.ToList();
        var routes = CollectPublicRoutes(all);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // by HOST
        var entries = new List<Entry>();
        var secrets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (stackName, stack) in all)
        {
            foreach (var m in stack.Members)
                foreach (var svc in m.Metadata.Services)
                    entries.Add(ToEntry(svc, stackName, m.Metadata.Name, m.Metadata.Services.Count == 1, $"CT {m.Spec.Ctid}", m.Spec.Node, routes, claimed, secrets));
            foreach (var v in stack.VmMembers)
                foreach (var svc in v.Metadata.Services)
                    entries.Add(ToEntry(svc, stackName, v.Metadata.Name, v.Metadata.Services.Count == 1, $"VM {v.Spec.Vmid}", v.Spec.Node, routes, claimed, secrets));
        }

        var unassigned = routes.Where(r => !claimed.Contains(r.Host)).ToList();
        foreach (var r in unassigned)
            entries.Add(new Entry(UnassignedGroup, r.Host, $"https://{r.Host}", null,
                $"Exposed via {r.Gate} — no shape declares it under metadata.services", null, null));

        return new Model(
            entries.OrderBy(e => e.Group == UnassignedGroup ? 1 : 0).ThenBy(e => e.Group, StringComparer.Ordinal)
                   .ThenBy(e => e.Name, StringComparer.Ordinal).ToList(),
            unassigned, secrets);
    }

    private static Entry ToEntry(
        DashboardService svc, string stackName, string memberName, bool soleService, string guest, string? node,
        List<PublicRoute> routes, HashSet<string> claimed, HashSet<string> secrets)
    {
        var name = string.IsNullOrWhiteSpace(svc.Name) ? memberName : svc.Name;
        var group = string.IsNullOrWhiteSpace(svc.Group) ? stackName : svc.Group!;
        var icon = string.IsNullOrWhiteSpace(svc.Icon) ? Normalise(name) : svc.Icon;

        // Public routes: an explicit `public:` claims the route with that host (and shows its
        // gate if one is declared); otherwise every route whose keys include the service name —
        // or the shape name, but only when the shape declares a single service, so a host with
        // several UIs does not pin all of its exposures on the first one. Claimed hosts are not
        // reported as undeclared.
        string? publicPart = null;
        if (!string.IsNullOrWhiteSpace(svc.Public))
        {
            var host = Uri.TryCreate(svc.Public, UriKind.Absolute, out var u) ? u.Host : svc.Public!;
            var hit = routes.FirstOrDefault(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) { claimed.Add(hit.Host); publicPart = $"{hit.Host} ({hit.Gate})"; }
            else publicPart = $"public {svc.Public}";
        }
        else
        {
            var keys = new HashSet<string>(StringComparer.Ordinal) { Normalise(name) };
            if (soleService) keys.Add(Normalise(memberName));
            var hits = routes.Where(r => r.Keys.Overlaps(keys)).ToList();
            foreach (var h in hits) claimed.Add(h.Host);
            if (hits.Count > 0)
                publicPart = string.Join(", ", hits.Select(h => $"{h.Host} ({h.Gate})"));
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(svc.Description)) parts.Add(svc.Description!.Trim());
        parts.Add(string.IsNullOrWhiteSpace(node) ? guest : $"{guest} on {node}");
        if (publicPart is not null) parts.Add(publicPart);

        var widget = RenderWidget(svc.Widget, svc.Url, secrets);
        return new Entry(group, name, svc.Url, icon, string.Join(" · ", parts), svc.Url, widget);
    }

    // The widget map verbatim, except `<x>From: NAME` → `<x>: {{HOMEPAGE_VAR_NAME}}`, and a
    // defaulted `url`. Key order is preserved so the render is stable.
    private static IReadOnlyDictionary<string, object?>? RenderWidget(
        Dictionary<string, object?>? widget, string serviceUrl, HashSet<string> secrets)
    {
        if (widget is null || widget.Count == 0) return null;
        var outp = new Dictionary<string, object?>();
        foreach (var (k, v) in widget)
        {
            if (k.EndsWith("From", StringComparison.Ordinal) && k.Length > 4 && v is not null)
            {
                var secret = v.ToString()!.Trim();
                secrets.Add(secret);
                outp[k[..^4]] = $"{{{{HOMEPAGE_VAR_{secret}}}}}";
            }
            else outp[k] = v;
        }
        if (!outp.ContainsKey("url")) outp["url"] = serviceUrl;
        return outp;
    }

    // Every public exposure across the stacks, keyed for name matching.
    internal static List<PublicRoute> CollectPublicRoutes(IEnumerable<(string StackName, ShapeLoader.LoadedStack Stack)> stacks)
    {
        var byHost = new Dictionary<string, (string Gate, HashSet<string> Keys)>(StringComparer.OrdinalIgnoreCase);
        void Add(string host, string gate, params string[] keys)
        {
            if (!byHost.TryGetValue(host, out var r)) byHost[host] = r = (gate, new HashSet<string>(StringComparer.Ordinal));
            foreach (var k in keys) if (k.Length > 0) r.Keys.Add(k);
        }
        foreach (var (_, stack) in stacks)
            foreach (var m in stack.Members)
            {
                var c = m.Spec.Config;
                var isPangolin = string.Equals(m.Spec.Provisioner ?? m.Spec.App, "pangolin", StringComparison.OrdinalIgnoreCase);
                if (isPangolin && c.TryGetValue("resources", out var rv) && rv is IEnumerable<object> res)
                {
                    var baseDomain = c.TryGetValue("baseDomain", out var bd) ? bd?.ToString() ?? "" : "";
                    foreach (var o in res)
                    {
                        if (o is not IDictionary r) continue;
                        var sub = r["subdomain"]?.ToString() ?? "";
                        var zone = r["zone"]?.ToString();
                        var domain = r["domain"]?.ToString() is { Length: > 0 } d ? d : baseDomain;
                        var host = string.IsNullOrEmpty(zone) ? $"{sub}.{domain}" : $"{sub}.{zone}.{domain}";
                        var sso = !(r["sso"] is { } raw && bool.TryParse(raw.ToString(), out var b) && !b);
                        // Both the display name and the subdomain are match keys.
                        Add(host, sso ? "Pangolin SSO" : "Pangolin, app login only", Normalise(r["name"]?.ToString() ?? sub), Normalise(sub));
                    }
                }
                if (string.Equals(m.Spec.App, "cloudflared", StringComparison.OrdinalIgnoreCase)
                    && c.TryGetValue("ingress", out var iv) && iv is IEnumerable<object> ing)
                {
                    foreach (var o in ing)
                    {
                        if (o is not IDictionary r || r["hostname"]?.ToString() is not { Length: > 0 } host) continue;
                        var isPublic = r["public"] is { } p && bool.TryParse(p.ToString(), out var pb) && pb;
                        Add(host, isPublic ? "Cloudflare tunnel, no gate" : "Cloudflare Access", Normalise(host.Split('.')[0]));
                    }
                }
            }
        return byHost.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                     .Select(kv => new PublicRoute(kv.Key, kv.Value.Gate, kv.Value.Keys)).ToList();
    }

    // Case- and punctuation-insensitive match key: "Home Assistant (CT 6005)" → "homeassistantct6005",
    // "home-assistant" → "homeassistant". Parenthesised suffixes are dropped first so a name
    // annotated in Pangolin still matches the plain service name.
    internal static string Normalise(string s)
    {
        s = Regex.Replace(s, @"\s*\(.*?\)\s*", "");
        return Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
    }

    // ── check ────────────────────────────────────────────────────────────────────

    // Problems a human must act on. `exportedVars` are the HOMEPAGE_VAR_* names the dashboard
    // host's quadlet exports (see ExportedVars).
    public static IReadOnlyList<string> Check(Model m, IEnumerable<string> exportedVars)
    {
        var exported = new HashSet<string>(exportedVars, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var r in m.UnassignedRoutes)
            problems.Add($"{r.Host} is exposed ({r.Gate}) but no shape declares it under metadata.services — add an entry named to match");
        foreach (var s in m.SecretRefs.OrderBy(x => x, StringComparer.Ordinal))
            if (!exported.Contains($"HOMEPAGE_VAR_{s}"))
                problems.Add($"a widget references secret {s}, but the dashboard host's quadlet does not export HOMEPAGE_VAR_{s}");
        return problems;
    }

    // `Secret=<name>,type=env,target=HOMEPAGE_VAR_X` lines of a quadlet → the exported var names.
    public static IReadOnlyList<string> ExportedVars(string quadletText) =>
        Regex.Matches(quadletText, @"^\s*Secret=.*?target=(HOMEPAGE_VAR_[A-Za-z0-9_]+)", RegexOptions.Multiline)
             .Select(mm => mm.Groups[1].Value).Distinct().ToList();

    // ── render ───────────────────────────────────────────────────────────────────

    // Homepage's services.yaml: a list of single-key group maps, each a list of single-key
    // service maps. Hand-emitted rather than serialised: the structure is fixed, every scalar
    // is double-quoted (so "{{HOMEPAGE_VAR_X}}" survives, and so does a description with a
    // colon), and the output is byte-stable for the deploy step's change detection.
    public static string Render(Model m)
    {
        var sb = new StringBuilder();
        sb.Append("# GENERATED by `homelab-infra dashboard` from every stack's metadata.services (ADR-0012).\n");
        sb.Append("# Do not edit: the next merge to main re-renders and overwrites this file.\n");
        if (m.UnassignedRoutes.Count > 0)
            sb.Append($"# {m.UnassignedRoutes.Count} public route(s) have no declaring shape — see the last group.\n");
        foreach (var g in m.Entries.GroupBy(e => e.Group))
        {
            sb.Append($"- {Q(g.Key)}:\n");
            foreach (var e in g)
            {
                sb.Append($"    - {Q(e.Name)}:\n");
                sb.Append($"        href: {Q(e.Href)}\n");
                if (e.Icon is not null) sb.Append($"        icon: {Q(e.Icon)}\n");
                sb.Append($"        description: {Q(e.Description)}\n");
                if (e.SiteMonitor is not null) sb.Append($"        siteMonitor: {Q(e.SiteMonitor)}\n");
                if (e.Widget is not null)
                {
                    sb.Append("        widget:\n");
                    foreach (var (k, v) in e.Widget) EmitScalarOrList(sb, k, v, 10);
                }
            }
        }
        return sb.ToString();
    }

    private static void EmitScalarOrList(StringBuilder sb, string key, object? v, int indent)
    {
        var pad = new string(' ', indent);
        switch (v)
        {
            case null: sb.Append($"{pad}{key}: null\n"); break;
            case bool b: sb.Append($"{pad}{key}: {(b ? "true" : "false")}\n"); break;
            case int or long: sb.Append($"{pad}{key}: {v}\n"); break;
            case string s when long.TryParse(s, out var n): sb.Append($"{pad}{key}: {n}\n"); break;  // YAML gave us "2"
            case string s when s is "true" or "false": sb.Append($"{pad}{key}: {s}\n"); break;
            case IDictionary d:
                sb.Append($"{pad}{key}:\n");
                foreach (var k in d.Keys) EmitScalarOrList(sb, k.ToString()!, d[k], indent + 2);
                break;
            case IEnumerable list when v is not string:
                sb.Append($"{pad}{key}:\n");
                foreach (var item in list) sb.Append($"{pad}  - {Q(item?.ToString() ?? "")}\n");
                break;
            default: sb.Append($"{pad}{key}: {Q(v.ToString()!)}\n"); break;
        }
    }

    private static string Q(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
