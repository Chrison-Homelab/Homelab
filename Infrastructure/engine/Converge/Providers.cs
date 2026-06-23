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
            $"{{\"type\":\"CNAME\",\"name\":\"{fqdn}\",\"content\":\"{content}\",\"proxied\":true,\"comment\":\"managed by homelab-infra converge\"}}",
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
}
