using System.Net;
using System.Text.Json;

namespace Homelab.Infrastructure.Converge;

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

    // Returns true on "Ok." — qBittorrent's success body for a valid login.
    public async Task<bool> LoginAsync(string user, string pass, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = pass });
        using var resp = await _http.PostAsync("api/v2/auth/login", form, ct);
        if (!resp.IsSuccessStatusCode) return false;
        return (await resp.Content.ReadAsStringAsync(ct)).Trim() == "Ok.";
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
