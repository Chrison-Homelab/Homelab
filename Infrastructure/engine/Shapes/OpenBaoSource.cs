using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Homelab.Infrastructure.Shapes;

// Reads secret/homelab/<KEY> from the homelab OpenBao (DevOps CT 3007, #609 step 4), so a converge
// takes its secrets from the store directly instead of from whatever secrets.env a checkout
// happens to hold. Stale secrets.env copies were a recurring failure: a blank PLEX_TOKEN from an
// old template, and two failed deploys on 2026-09-28 from a copied file that predated HOME_WAN_IP.
//
// TRUST: TLS is pinned to scripts/openbao-ca.pem (the CT's own self-signed cert), never skipped.
// AUTH: OPENBAO_TOKEN if set, else this workstation's AppRole from the macOS Keychain (the same
// items scripts/openbao-setup-workstation.sh stores). A token we minted is revoked when done.
// Values never leave the returned map: nothing here logs one.
public sealed class OpenBaoSource
{
    public const string DefaultAddr = "https://openbao.devops.chrison.internal:8200";
    public const string KvPrefix = "homelab";

    private readonly HttpClient _http;
    private readonly Func<string, string?> _keychain;

    internal OpenBaoSource(HttpClient http, Func<string, string?> keychain)
    {
        _http = http;
        _keychain = keychain;
    }

    // (map, null) on success; (null, reason) when OpenBao cannot be used. Never throws.
    public static (Dictionary<string, string>? Map, string? Reason) TryLoad(string? repoRoot)
    {
        var addr = Environment.GetEnvironmentVariable("OPENBAO_ADDR") ?? DefaultAddr;
        var caPath = repoRoot is null ? null : Path.Combine(repoRoot, "scripts", "openbao-ca.pem");
        if (caPath is null || !File.Exists(caPath)) return (null, "no pinned certificate (scripts/openbao-ca.pem)");
        try
        {
            using var http = PinnedClient(addr, caPath);
            var src = new OpenBaoSource(http, Keychain);
            return src.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return (null, $"unreachable ({ex.GetType().Name})");
        }
    }

    internal async Task<(Dictionary<string, string>? Map, string? Reason)> LoadAsync(CancellationToken ct)
    {
        // seal-status needs no token, and tells the cases that need different human action apart.
        using (var st = await _http.GetAsync("v1/sys/seal-status", ct))
        {
            if (!st.IsSuccessStatusCode) return (null, $"seal-status HTTP {(int)st.StatusCode}");
            using var doc = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.GetProperty("initialized").GetBoolean()) return (null, "not initialised");
            if (doc.RootElement.GetProperty("sealed").GetBoolean())
                return (null, "SEALED — unseal on CT 3007 (2 shares from Bitwarden)");
        }

        var token = Environment.GetEnvironmentVariable("OPENBAO_TOKEN");
        var minted = false;
        if (string.IsNullOrEmpty(token))
        {
            var roleId = _keychain("homelab-openbao-role-id");
            var secretId = _keychain("homelab-openbao-secret-id");
            if (string.IsNullOrEmpty(roleId) || string.IsNullOrEmpty(secretId))
                return (null, "no AppRole in Keychain — run scripts/openbao-setup-workstation.sh");
            var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["role_id"] = roleId, ["secret_id"] = secretId });
            using var login = await _http.PostAsync("v1/auth/approle/login", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!login.IsSuccessStatusCode) return (null, $"AppRole login refused (HTTP {(int)login.StatusCode})");
            using var ldoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync(ct));
            token = ldoc.RootElement.GetProperty("auth").GetProperty("client_token").GetString();
            minted = true;
        }

        try
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            using var list = await Send(HttpMethod.Parse("LIST"), $"v1/secret/metadata/{KvPrefix}", token!, ct);
            if ((int)list.StatusCode == 404) return (map, null);   // empty store: valid, just nothing in it
            if (!list.IsSuccessStatusCode) return (null, $"list HTTP {(int)list.StatusCode}");
            using var ldoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct));
            foreach (var k in ldoc.RootElement.GetProperty("data").GetProperty("keys").EnumerateArray())
            {
                var key = k.GetString();
                if (string.IsNullOrEmpty(key) || key.EndsWith('/')) continue;
                using var get = await Send(HttpMethod.Get, $"v1/secret/data/{KvPrefix}/{key}", token!, ct);
                if (!get.IsSuccessStatusCode) return (null, $"read {key} HTTP {(int)get.StatusCode}");
                using var gdoc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct));
                if (gdoc.RootElement.GetProperty("data").GetProperty("data").TryGetProperty("value", out var v)
                    && v.ValueKind == JsonValueKind.String)
                    map[key] = v.GetString()!;
            }
            return (map, null);
        }
        finally
        {
            if (minted)
                try { using var _ = await Send(HttpMethod.Post, "v1/auth/token/revoke-self", token!, ct); } catch { /* best effort */ }
        }
    }

    private Task<HttpResponseMessage> Send(HttpMethod m, string path, string token, CancellationToken ct)
    {
        var req = new HttpRequestMessage(m, path);
        req.Headers.Add("X-Vault-Token", token);
        return _http.SendAsync(req, ct);
    }

    // Trust exactly the pinned certificate, and still require the host name to match it.
    private static HttpClient PinnedClient(string addr, string caPath)
    {
        // Cert-only loader: CreateFromPemFile expects a private key in the file too and throws without one.
        var pinned = X509CertificateLoader.LoadCertificateFromFile(caPath);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is null) return false;
                if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(pinned);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri(addr.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    // macOS Keychain via `security`; absent elsewhere (CI uses OPENBAO_TOKEN or the process env).
    private static string? Keychain(string service)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var psi = new ProcessStartInfo("security", $"find-generic-password -a openbao -s {service} -w")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && o.Length > 0 ? o : null;
        }
        catch { return null; }
    }
}
