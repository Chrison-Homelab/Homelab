#!/usr/bin/env pwsh
#
# secrets-sync.ps1 — regenerate secrets.env from secrets.env.template, filling the
# blank (secret) keys from OpenBao (DevOps CT 3007, secret/homelab/<KEY>, field value).
# PowerShell twin of secrets-sync.sh — produces a byte-identical, bash-sourceable
# secrets.env (LF line endings, single-quoted values).
#
#   • OpenBao is the only store since #609 (Bitwarden SM frozen 2026-10-03, no longer read).
#   • Non-secret template lines pass through verbatim; blank keys are filled.
#   • Any blank template key NOT found in OpenBao is left blank and reported LOUDLY.
#   • No secret value is ever printed. TLS is pinned to scripts/openbao-ca.pem, never skipped.
#
# Token resolution (first hit wins) — on Windows there is no Keychain AppRole, so log in as a human:
#   1. $env:OPENBAO_TOKEN / $env:BAO_TOKEN
#   2. ~/.vault-token, which `bao login -method=oidc` writes (browser login through authentik):
#        $env:BAO_ADDR = 'https://openbao.devops.chrison.internal:8200'
#        $env:BAO_CACERT = "$PWD/scripts/openbao-ca.pem"
#        bao login -method=oidc
#
# Usage:
#   ./scripts/secrets-sync.ps1                        # writes ./secrets.env
#   ./scripts/secrets-sync.ps1 /tmp/out               # custom output path (for testing)
#   ./scripts/secrets-sync.ps1 <out> <template>       # custom output AND template (other stacks)
[CmdletBinding()]
param([string]$OutPath, [string]$TemplatePath)

$ErrorActionPreference = 'Stop'
$Addr      = if ($env:OPENBAO_ADDR) { $env:OPENBAO_ADDR.TrimEnd('/') } else { 'https://openbao.devops.chrison.internal:8200' }
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$Template  = if ($TemplatePath) { $TemplatePath } else { Join-Path $RepoRoot 'secrets.env.template' }
if (-not $OutPath) { $OutPath = Join-Path $RepoRoot 'secrets.env' }

if (-not (Test-Path -LiteralPath $Template)) { throw "template not found: $Template" }

# ── token ──
$token = if ($env:OPENBAO_TOKEN) { $env:OPENBAO_TOKEN } elseif ($env:BAO_TOKEN) { $env:BAO_TOKEN } else { $null }
if (-not $token) {
  $tokFile = Join-Path $HOME '.vault-token'
  if (Test-Path -LiteralPath $tokFile) { $token = (Get-Content -LiteralPath $tokFile -Raw).Trim() }
}
if (-not $token) { throw "no OpenBao token: run 'bao login -method=oidc' (see the header of this script)" }

# ── HTTP client trusting exactly the pinned certificate (host name still checked) ──
# Compiled, not a script block: the TLS callback runs on a .NET thread with no PowerShell runspace,
# where a script-block delegate fails ("The SSL connection could not be established").
if (-not ('HomelabPinnedTls' -as [type])) {
  Add-Type -TypeDefinition @'
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class HomelabPinnedTls {
  public static HttpClientHandler Handler(string pemPath) {
    var pinned = X509CertificateLoader.LoadCertificateFromFile(pemPath);
    return new HttpClientHandler {
      // Host name checked HERE, not via the errors flags: on macOS (.NET 9) Apple's policy flags
      // this long-lived self-signed cert as a name mismatch even though its SAN matches.
      ServerCertificateCustomValidationCallback = (req, cert, chain, errors) => {
        if (cert == null || req.RequestUri == null || !cert.MatchesHostname(req.RequestUri.IdnHost)) return false;
        using var c = new X509Chain();
        c.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        c.ChainPolicy.CustomTrustStore.Add(pinned);
        c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return c.Build(cert);
      }
    };
  }
}
'@
}
$handler = [HomelabPinnedTls]::Handler((Join-Path $RepoRoot 'scripts/openbao-ca.pem'))
$http = [System.Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(15)
function Invoke-Bao([string]$Method, [string]$Path) {
  $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$Addr/v1/$Path")
  $req.Headers.Add('X-Vault-Token', $token)
  $res = $http.SendAsync($req).GetAwaiter().GetResult()
  [pscustomobject]@{ Code = [int]$res.StatusCode; Body = $res.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
}

$seal = Invoke-Bao 'GET' 'sys/seal-status'
if ($seal.Code -ne 200) { throw "OpenBao seal-status HTTP $($seal.Code)" }
if (($seal.Body | ConvertFrom-Json).sealed) { throw "OpenBao is SEALED - unseal CT 3007 (2 shares from Bitwarden)" }

# ── pull all secrets once ──
$sm = @{}
$list = Invoke-Bao 'LIST' 'secret/metadata/homelab'
if ($list.Code -eq 403) { throw "OpenBao refused the token (expired? run 'bao login -method=oidc' again)" }
if ($list.Code -ne 200 -and $list.Code -ne 404) { throw "OpenBao list HTTP $($list.Code)" }
if ($list.Code -eq 200) {
  foreach ($k in ($list.Body | ConvertFrom-Json).data.keys) {
    if ($k.EndsWith('/')) { continue }
    $g = Invoke-Bao 'GET' "secret/data/homelab/$k"
    if ($g.Code -ne 200) { throw "OpenBao read $k HTTP $($g.Code)" }
    $sm[$k] = [string](($g.Body | ConvertFrom-Json).data.data.value)
  }
}

# single-quote a value for safe `set -a; . secrets.env` sourcing (bash rules)
function Quote-Sh([string]$v) { "'" + ($v -replace "'", "'\''") + "'" }

$filled = 0; $pass = 0; $missing = @()
$out = New-Object System.Collections.Generic.List[string]
foreach ($line in (Get-Content -LiteralPath $Template)) {
  if ($line -match '^(\s*)([A-Za-z_][A-Za-z0-9_]*)=$') {
    $indent = $Matches[1]; $key = $Matches[2]
    if ($sm.ContainsKey($key)) { $out.Add("$indent$key=$(Quote-Sh ([string]$sm[$key]))"); $filled++ }
    else { $out.Add($line); $missing += $key }
  } else { $out.Add($line); $pass++ }
}

# write LF-terminated, UTF-8 no BOM (matches the .sh output)
$text = ($out -join "`n") + "`n"
[System.IO.File]::WriteAllText($OutPath, $text, (New-Object System.Text.UTF8Encoding($false)))

# best-effort restrictive perms
try {
  if ($IsWindows) {
    icacls $OutPath /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
  } else { & chmod 600 $OutPath }
} catch { }

Write-Host "secrets.env written -> $OutPath"
Write-Host "  filled from OpenBao: $filled   passthrough lines: $pass"
if ($missing.Count -gt 0) {
  Write-Warning ("MISSING from OpenBao (left blank): " + ($missing -join ' '))
  Write-Warning "  -> add them with scripts/openbao-set.sh KEY (from a Mac/Linux box), then re-run."
  exit 2
}
