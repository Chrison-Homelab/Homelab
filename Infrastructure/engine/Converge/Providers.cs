using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Homelab.Infrastructure.Converge;

// Thin REST clients for the external providers converge derives secrets from.
// Read methods power idempotency checks; create methods are ADD-ONLY (callers
// must check existence first — see CLAUDE.md "shared external accounts").

public sealed class GithubApi
{
    private readonly HttpClient _http;

    public GithubApi(string pat)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("homelab-infra/1.0");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<bool> IsOrgRunnerOnlineAsync(string org, string name, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"orgs/{org}/actions/runners?per_page=100", ct));
        foreach (var r in doc.RootElement.GetProperty("runners").EnumerateArray())
            if (r.GetProperty("name").GetString() == name && r.GetProperty("status").GetString() == "online")
                return true;
        return false;
    }

    public async Task<string> CreateOrgRunnerTokenAsync(string org, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"orgs/{org}/actions/runners/registration-token", null, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("token").GetString()!;
    }
}

public sealed record CfZone(string ZoneId, string AccountId);
public sealed record CfDnsRecord(string Id, string Name, string Content, string Comment);
public sealed record CfAccessApp(string Id, string Name, string Domain);
// An Access policy plus the IP CIDRs it includes — enough to tell whether a shape's
// `access.bypass` list still matches live (the policy is keyed by name, its content isn't).
public sealed record CfAccessPolicy(string Id, string Name, IReadOnlyList<string> IncludeIps);

public sealed class CloudflareApi
{
    private readonly HttpClient _http;

    public CloudflareApi(string token)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.cloudflare.com/client/v4/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<JsonElement> ResultAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.GetProperty("success").GetBoolean())
            throw new InvalidOperationException($"Cloudflare API error: {body}");
        return doc.RootElement.GetProperty("result").Clone();
    }

    public async Task<CfZone> GetZoneAsync(string name, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"zones?name={name}", ct), ct);
        var z = r.EnumerateArray().FirstOrDefault();
        if (z.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"zone '{name}' not visible to token");
        return new CfZone(z.GetProperty("id").GetString()!, z.GetProperty("account").GetProperty("id").GetString()!);
    }

    public async Task<string?> FindTunnelIdAsync(string accountId, string name, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"accounts/{accountId}/cfd_tunnel?is_deleted=false", ct), ct);
        foreach (var t in r.EnumerateArray())
            if (t.GetProperty("name").GetString() == name) return t.GetProperty("id").GetString();
        return null;
    }

    public async Task<bool> DnsExistsAsync(string zoneId, string fqdn, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"zones/{zoneId}/dns_records?name={fqdn}", ct), ct);
        return r.GetArrayLength() > 0;
    }

    // --- Reconcile reads: list what WE manage so a converge can prune drift (#195) ---

    // CNAME records in the zone (id/name/content/comment). Lets the caller prune
    // managed CNAMEs (comment == ManagedComment) that point at OUR tunnel but whose
    // host is no longer in the shape — hand-managed records (other/empty comment)
    // are left untouched (CLAUDE.md add-only rule).
    public async Task<List<CfDnsRecord>> ListCnamesAsync(string zoneId, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"zones/{zoneId}/dns_records?type=CNAME&per_page=100", ct), ct);
        var list = new List<CfDnsRecord>();
        foreach (var d in r.EnumerateArray())
            list.Add(new CfDnsRecord(
                d.GetProperty("id").GetString()!,
                d.GetProperty("name").GetString()!,
                d.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                d.TryGetProperty("comment", out var cm) && cm.ValueKind == JsonValueKind.String ? cm.GetString() ?? "" : ""));
        return list;
    }

    // Self-hosted Access apps (id/name/domain) — lets the caller prune apps WE named
    // ("<sub> (<Stack>)") whose domain left the shape. Other apps are left untouched.
    public async Task<List<CfAccessApp>> ListAccessAppsAsync(string accountId, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"accounts/{accountId}/access/apps?per_page=100", ct), ct);
        var list = new List<CfAccessApp>();
        foreach (var a in r.EnumerateArray())
            list.Add(new CfAccessApp(
                a.GetProperty("id").GetString()!,
                a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                a.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : ""));
        return list;
    }

    // --- Reconcile deletes: only ever called on resources WE manage (see callers) ---

    public async Task DeleteDnsRecordAsync(string zoneId, string recordId, CancellationToken ct)
        => await ResultAsync(await _http.DeleteAsync($"zones/{zoneId}/dns_records/{recordId}", ct), ct);

    public async Task DeleteAccessAppAsync(string accountId, string appId, CancellationToken ct)
        => await ResultAsync(await _http.DeleteAsync($"accounts/{accountId}/access/apps/{appId}", ct), ct);

    // The comment stamped on every CNAME converge creates — the prune guard.
    public const string ManagedComment = "managed by homelab-infra converge";

    // --- ADD-ONLY mutations (callers must check existence first) ---

    public async Task<string> CreateTunnelAsync(string accountId, string name, CancellationToken ct)
    {
        var body = new StringContent($"{{\"name\":\"{name}\",\"config_src\":\"cloudflare\"}}", Encoding.UTF8, "application/json");
        var r = await ResultAsync(await _http.PostAsync($"accounts/{accountId}/cfd_tunnel", body, ct), ct);
        return r.GetProperty("id").GetString()!;
    }

    public async Task SetTunnelConfigAsync(string accountId, string tunnelId, string ingressJson, CancellationToken ct)
    {
        var body = new StringContent($"{{\"config\":{{\"ingress\":{ingressJson}}}}}", Encoding.UTF8, "application/json");
        await ResultAsync(await _http.PutAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", body, ct), ct);
    }

    // Live ingress (hostname → service) of a tunnel, for content-aware idempotency (#165).
    // Skips the catch-all (entries without a hostname).
    public async Task<List<(string host, string service)>> GetTunnelIngressAsync(string accountId, string tunnelId, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", ct), ct);
        var list = new List<(string, string)>();
        if (r.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object
            && cfg.TryGetProperty("ingress", out var ing) && ing.ValueKind == JsonValueKind.Array)
            foreach (var e in ing.EnumerateArray())
                if (e.TryGetProperty("hostname", out var h) && h.ValueKind == JsonValueKind.String)
                    list.Add((h.GetString()!, e.TryGetProperty("service", out var sv) ? sv.GetString() ?? "" : ""));
        return list;
    }

    public async Task<string> GetTunnelTokenAsync(string accountId, string tunnelId, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}/token", ct), ct);
        return r.GetString()!;
    }

    public async Task CreateCnameAsync(string zoneId, string fqdn, string content, CancellationToken ct)
    {
        var body = new StringContent(
            $"{{\"type\":\"CNAME\",\"name\":\"{fqdn}\",\"content\":\"{content}\",\"proxied\":true,\"comment\":\"{ManagedComment}\"}}",
            Encoding.UTF8, "application/json");
        await ResultAsync(await _http.PostAsync($"zones/{zoneId}/dns_records", body, ct), ct);
    }

    // Grey-cloud (DNS-only, proxied:false) A record — for the *.lab / *.arr wildcard
    // zones that point straight at the home WAN IP (ADR-0007). proxied MUST be false:
    // an orange-cloud record would hand TLS back to Cloudflare and re-impose its
    // one-level wildcard limit. Carries the ManagedComment so #195's prune ignores it.
    public async Task CreateARecordAsync(string zoneId, string fqdn, string ip, CancellationToken ct)
    {
        var body = new StringContent(
            $"{{\"type\":\"A\",\"name\":\"{fqdn}\",\"content\":\"{ip}\",\"proxied\":false,\"comment\":\"{ManagedComment}\"}}",
            Encoding.UTF8, "application/json");
        await ResultAsync(await _http.PostAsync($"zones/{zoneId}/dns_records", body, ct), ct);
    }

    // --- Cloudflare Access (Zero Trust) gating — ADD-ONLY (callers check first) ---
    // A self-hosted Access application keyed by its `domain`, plus an allow-by-email
    // policy. With no other IdP configured these emails authenticate via One-Time PIN.
    // Mirrors the Topaz AccessReconciler over the same endpoints.

    public async Task<string?> FindAccessAppIdAsync(string accountId, string domain, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"accounts/{accountId}/access/apps?per_page=100", ct), ct);
        foreach (var a in r.EnumerateArray())
            if (string.Equals(a.GetProperty("domain").GetString(), domain, StringComparison.OrdinalIgnoreCase))
                return a.GetProperty("id").GetString();
        return null;
    }

    public async Task<string> CreateAccessAppAsync(string accountId, string name, string domain, string sessionDuration, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            name,
            domain,
            type = "self_hosted",
            session_duration = sessionDuration,
            app_launcher_visible = true,
        });
        var body = new StringContent(json, Encoding.UTF8, "application/json");
        var r = await ResultAsync(await _http.PostAsync($"accounts/{accountId}/access/apps", body, ct), ct);
        return r.GetProperty("id").GetString()!;
    }

    public async Task<bool> AccessPolicyExistsAsync(string accountId, string appId, string policyName, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"accounts/{accountId}/access/apps/{appId}/policies", ct), ct);
        foreach (var p in r.EnumerateArray())
            if (p.GetProperty("name").GetString() == policyName) return true;
        return false;
    }

    public async Task CreateAccessAllowEmailPolicyAsync(string accountId, string appId, string policyName, IReadOnlyList<string> emails, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            name = policyName,
            decision = "allow",
            include = emails.Select(e => new { email = new { email = e } }).ToArray(),
        });
        var body = new StringContent(json, Encoding.UTF8, "application/json");
        await ResultAsync(await _http.PostAsync($"accounts/{accountId}/access/apps/{appId}/policies", body, ct), ct);
    }

    // The named policy on an app together with the IP CIDRs it includes, or null if absent.
    // Existence alone is not enough for the bypass policy: its CIDR list is the thing the
    // shape declares, so a converge has to be able to see the live list and correct it.
    public async Task<CfAccessPolicy?> GetAccessPolicyAsync(string accountId, string appId, string policyName, CancellationToken ct)
    {
        var r = await ResultAsync(await _http.GetAsync($"accounts/{accountId}/access/apps/{appId}/policies", ct), ct);
        foreach (var p in r.EnumerateArray())
        {
            if (p.GetProperty("name").GetString() != policyName) continue;
            var ips = new List<string>();
            if (p.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.Array)
                foreach (var e in inc.EnumerateArray())
                    if (e.TryGetProperty("ip", out var ipObj) && ipObj.ValueKind == JsonValueKind.Object
                        && ipObj.TryGetProperty("ip", out var ip) && ip.GetString() is { Length: > 0 } cidr)
                        ips.Add(cidr);
            return new CfAccessPolicy(p.GetProperty("id").GetString()!, policyName, ips);
        }
        return null;
    }

    // A `bypass` policy: requests from the given IP CIDRs skip authentication entirely
    // (no OTP) — used to exempt a trusted static IP (e.g. home) from the gate. The app's
    // OWN login (Proxmox/PDM/Pangolin/Seerr) still applies; this only drops the CF layer.
    // ADD-ONLY: a new named policy alongside the allow policy; callers check existence first.
    public async Task CreateAccessBypassIpPolicyAsync(string accountId, string appId, string policyName, IReadOnlyList<string> cidrs, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            name = policyName,
            decision = "bypass",
            include = cidrs.Select(c => new { ip = new { ip = c } }).ToArray(),
        });
        var body = new StringContent(json, Encoding.UTF8, "application/json");
        await ResultAsync(await _http.PostAsync($"accounts/{accountId}/access/apps/{appId}/policies", body, ct), ct);
    }

    // Rewrite an EXISTING bypass policy's CIDR list. The only mutation here that edits
    // rather than adds — and it is confined to a policy WE created, matched by our own
    // name (`bypass-trusted-ip`), on an app the caller already resolved. Without it,
    // `access.bypass` is write-once: the create is skipped forever once the policy
    // exists, so editing the list in a shape moves nothing (how the IPv4-only bypass
    // outlived the home network gaining IPv6).
    public async Task UpdateAccessBypassIpPolicyAsync(string accountId, string appId, string policyId, string policyName, IReadOnlyList<string> cidrs, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            name = policyName,
            decision = "bypass",
            include = cidrs.Select(c => new { ip = new { ip = c } }).ToArray(),
        });
        var body = new StringContent(json, Encoding.UTF8, "application/json");
        await ResultAsync(await _http.PutAsync($"accounts/{accountId}/access/apps/{appId}/policies/{policyId}", body, ct), ct);
    }
}
