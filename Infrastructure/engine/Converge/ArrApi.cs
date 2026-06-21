using System.Net;
using System.Text;
using System.Text.Json;

namespace Homelab.Infrastructure.Converge;

// Servarr REST client (Sonarr/Radarr v3, Prowlarr v1) — `X-Api-Key` + JSON. Used by
// the arr-wire provisioners; reads power idempotency (find-by-name), POSTs are
// create-if-missing (add-only — re-runs don't duplicate, and don't overwrite drift).
public sealed class ArrClient : IDisposable
{
    private readonly HttpClient _http;

    public ArrClient(string baseUrl, string apiKey)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            throw new ArgumentException($"arr base URL not parseable: '{baseUrl}' (resolved host/IP was empty or malformed)");
        _http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    // Returns the parsed JSON (array or object) for a GET; throws on transport error.
    public async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(path, ct));
        return doc.RootElement.Clone();
    }

    public async Task<(bool ok, string body)> PostAsync(string path, string json, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        return (resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    public async Task<(bool ok, string body)> PutAsync(string path, string json, CancellationToken ct)
    {
        using var resp = await _http.PutAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        return (resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    public void Dispose() => _http.Dispose();
}

// Bazarr settings client — Bazarr has no per-setting REST API like the *arr; its UI
// saves the whole settings form. GET returns nested JSON; writes are flattened
// `settings-<section>-<key>` form fields (partial POSTs merge). Auth header is X-API-KEY.
public sealed class BazarrClient : IDisposable
{
    private readonly HttpClient _http;

    public BazarrClient(string baseUrl, string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
    }

    public async Task<JsonElement> GetSettingsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("api/system/settings", ct));
        return doc.RootElement.Clone();
    }

    public async Task<bool> PostSettingsAsync(IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync("api/system/settings", new FormUrlEncodedContent(form), ct);
        return resp.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}

// Thin REST clients for the *arr media apps the arr-wire provisioners talk to
// (issue #159). Like Providers.cs: read methods power idempotency, writes are
// applied only for missing config. Each client targets one already-running CT
// over the homelab LAN (the converge runner resolves node→IP — issue #162).

// qBittorrent WebUI API v2 — cookie auth (SID) + form-encoded bodies. qBittorrent
// rejects cross-origin writes unless the Referer matches the WebUI host, so we set
// it on every request.
public sealed class QbitClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    public QbitClient(string baseUrl)
    {
        _base = new Uri(baseUrl.TrimEnd('/') + "/");
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        _http = new HttpClient(handler) { BaseAddress = _base };
        _http.DefaultRequestHeaders.Referrer = _base;        // CSRF: Referer must match host
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    // Success = a 2xx that isn't the literal "Fails." body. Older qBittorrent returns
    // 200 "Ok."; newer (CT 5104) returns 204 with an empty body + the QBT_SID cookie
    // (captured by the CookieContainer). A wrong password is 200 "Fails."; a ban is 403.
    public async Task<bool> LoginAsync(string user, string pass, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = pass });
        using var resp = await _http.PostAsync("api/v2/auth/login", form, ct);
        if (!resp.IsSuccessStatusCode) return false;
        return !(await resp.Content.ReadAsStringAsync(ct)).Contains("Fails", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetStringAsync(string path, CancellationToken ct) => await _http.GetStringAsync(path, ct);

    public async Task<bool> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(path, new FormUrlEncodedContent(form), ct);
        return resp.IsSuccessStatusCode;
    }

    // setPreferences takes a single `json` field holding a JSON object of prefs.
    public Task<bool> SetPreferencesAsync(object prefs, CancellationToken ct) =>
        PostFormAsync("api/v2/app/setPreferences", new() { ["json"] = JsonSerializer.Serialize(prefs) }, ct);

    public void Dispose() => _http.Dispose();
}
