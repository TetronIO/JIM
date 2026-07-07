# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Seeds a set of Trusted Certificate test data into a running JIM instance.

.DESCRIPTION
    Populates JIM's Trusted Certificate store with a small, deterministic set of self-signed
    certificates that exercise the store's UI states: healthy, expiring-soon, expired, and
    disabled. Handy after a factory reset, which wipes configuration data including Trusted
    Certificates.

    The script ensures its own prerequisites before seeding:

      1. A .env file exists at the repository root (created from .env.example if absent).
      2. JIM_INFRASTRUCTURE_API_KEY is present in .env (a secure key is generated and written
         if absent, placeholder, or commented out).

    The infrastructure API key is only minted in the database when JIM.Web starts up (see
    Program.cs > InitialiseInfrastructureApiKeyAsync). So when this script has to generate a
    fresh key, the running JIM.Web container will not yet know it. In that case the script
    recreates the jim.web container to seed the key, unless -SkipWebRecreate is given. When an
    existing, valid key is already present the container is left untouched.

    Certificates are generated in-process (no OpenSSL dependency) and uploaded as public DER
    data via Add-JIMCertificate, so nothing needs to be staged on the server's filesystem.

    Re-running is safe: certificates are matched by name and skipped if already present. Use
    -Force to delete and recreate the seed set (for example, to reset their validity windows).

.PARAMETER Url
    Base URL of the JIM instance. Defaults to http://localhost:5200.

.PARAMETER ApiKey
    An explicit API key to authenticate with. When supplied, the .env / key-generation
    prerequisite steps are skipped entirely and this key is used as-is.

.PARAMETER Force
    Delete any existing seed certificates (matched by name) and recreate them.

.PARAMETER SkipWebRecreate
    Do not recreate the jim.web container even when a new infrastructure key was generated.
    Use this if you manage the stack yourself; you must restart JIM.Web so the new key is
    seeded before the script can authenticate.

.EXAMPLE
    ./test/scripts/Seed-TrustedCertificates.ps1

    Ensures prerequisites, then seeds the Trusted Certificate test data against the local stack.

.EXAMPLE
    ./test/scripts/Seed-TrustedCertificates.ps1 -Force

    Removes the existing seed certificates and recreates them with fresh validity windows.

.EXAMPLE
    ./test/scripts/Seed-TrustedCertificates.ps1 -Url https://jim.example.com -ApiKey $env:JIM_API_KEY

    Seeds a remote instance using an explicit API key (skips the .env prerequisite steps).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Url = "http://localhost:5200",

    [string]$ApiKey,

    [switch]$Force,

    [switch]$SkipWebRecreate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Paths ────────────────────────────────────────────────────────────────────
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$envFilePath = Join-Path $repoRoot ".env"
$envExamplePath = Join-Path $repoRoot ".env.example"
$modulePath = Join-Path $repoRoot "src" "JIM.PowerShell" "JIM.psd1"

# ── Small console helpers ────────────────────────────────────────────────────
function Write-Step { param([string]$Message) Write-Host $Message -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host "  [ok] $Message" -ForegroundColor Green }
function Write-Info { param([string]$Message) Write-Host "  $Message" -ForegroundColor Gray }
function Write-Warn { param([string]$Message) Write-Host "  [!] $Message" -ForegroundColor Yellow }

Write-Host ""
Write-Step "JIM Trusted Certificate test-data seeder"
Write-Host ""

# ─────────────────────────────────────────────────────────────────────────────
# Prerequisite 1 & 2: .env file and infrastructure API key
# Skipped entirely when an explicit -ApiKey was supplied.
# ─────────────────────────────────────────────────────────────────────────────
$keyChanged = $false

if ($ApiKey) {
    Write-Info "Using the API key supplied via -ApiKey; skipping .env prerequisite checks."
}
else {
    # --- .env exists -----------------------------------------------------------
    Write-Step "Checking prerequisites"

    if (-not (Test-Path $envFilePath)) {
        if (-not (Test-Path $envExamplePath)) {
            throw "Neither .env nor .env.example exists at $repoRoot. Cannot continue."
        }
        if ($PSCmdlet.ShouldProcess($envFilePath, "Create .env from .env.example")) {
            Copy-Item $envExamplePath $envFilePath
            Write-Ok "Created .env from .env.example."
        }
    }
    else {
        Write-Ok ".env present."
    }

    # --- API key present and usable -------------------------------------------
    # A usable key is an uncommented JIM_INFRASTRUCTURE_API_KEY that starts with 'jim_ak_',
    # is at least 32 characters, and is not the template placeholder.
    $envContent = Get-Content $envFilePath -Raw
    if ($null -eq $envContent) { $envContent = "" }

    $existingKey = $null
    if ($envContent -match "(?m)^\s*JIM_INFRASTRUCTURE_API_KEY=(.+)$") {
        $existingKey = $Matches[1].Trim()
    }

    $keyIsUsable = $existingKey -and
        $existingKey.StartsWith("jim_ak_") -and
        $existingKey.Length -ge 32 -and
        $existingKey -ne "jim_ak_your_generated_key_here"

    if ($keyIsUsable) {
        $ApiKey = $existingKey
        Write-Ok "JIM_INFRASTRUCTURE_API_KEY present (prefix: $($ApiKey.Substring(0, 12))...)."
    }
    else {
        # Generate a cryptographically secure key. RandomNumberGenerator, never System.Random.
        $randomBytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($randomBytes) } finally { $rng.Dispose() }
        $randomString = [Convert]::ToBase64String($randomBytes).Replace("+", "").Replace("/", "").Replace("=", "")
        $ApiKey = "jim_ak_$randomString"

        if ($PSCmdlet.ShouldProcess($envFilePath, "Write a generated JIM_INFRASTRUCTURE_API_KEY")) {
            if ($envContent -match "(?m)^\s*#?\s*JIM_INFRASTRUCTURE_API_KEY=.*") {
                # Replace an existing line, whether it was commented out or a placeholder.
                $envContent = $envContent -replace "(?m)^\s*#?\s*JIM_INFRASTRUCTURE_API_KEY=.*", "JIM_INFRASTRUCTURE_API_KEY=$ApiKey"
            }
            else {
                $prefix = if ($envContent.Length -gt 0 -and -not $envContent.EndsWith("`n")) { "`n" } else { "" }
                $envContent = $envContent + $prefix + "JIM_INFRASTRUCTURE_API_KEY=$ApiKey`n"
            }
            Set-Content -Path $envFilePath -Value $envContent -NoNewline
            $keyChanged = $true
            Write-Ok "Generated and wrote JIM_INFRASTRUCTURE_API_KEY (prefix: $($ApiKey.Substring(0, 12))...)."
            Write-Info "Note: infrastructure keys expire 24 hours after JIM.Web starts."
        }
    }
}

Write-Host ""

# ─────────────────────────────────────────────────────────────────────────────
# Load the JIM PowerShell module
# ─────────────────────────────────────────────────────────────────────────────
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at $modulePath."
}
Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
Import-Module $modulePath -Force -ErrorAction Stop

# ─────────────────────────────────────────────────────────────────────────────
# Recreate jim.web so a freshly-generated key is seeded (only when we changed it)
# ─────────────────────────────────────────────────────────────────────────────
function Invoke-JimWebRecreate {
    $composeFile = Join-Path $repoRoot "docker-compose.yml"
    $overrideFile = Join-Path $repoRoot "docker-compose.override.yml"
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Warn "Docker not found; cannot recreate jim.web automatically."
        return $false
    }

    Write-Info "Recreating jim.web to seed the new infrastructure key..."
    $composeArgs = @("compose", "-f", $composeFile)
    if (Test-Path $overrideFile) { $composeArgs += @("-f", $overrideFile) }
    $composeArgs += @("--profile", "with-db", "up", "-d", "--force-recreate", "jim.web")
    & docker @composeArgs 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "docker compose could not recreate jim.web (exit $LASTEXITCODE)."
        return $false
    }

    # Wait for the web app to answer again.
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -Method GET -TimeoutSec 2 -ErrorAction SilentlyContinue
            if ($response.StatusCode -in @(200, 302)) {
                Write-Ok "jim.web is ready."
                return $true
            }
        }
        catch {
            # Not ready yet; keep polling.
        }
        Start-Sleep -Seconds 2
    }
    Write-Warn "jim.web did not become ready within the timeout."
    return $false
}

if ($keyChanged -and -not $SkipWebRecreate) {
    Write-Step "Applying the new API key"
    [void](Invoke-JimWebRecreate)
    Write-Host ""
}

# ─────────────────────────────────────────────────────────────────────────────
# Connect
# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Connecting to JIM at $Url"
try {
    Connect-JIM -Url $Url -ApiKey $ApiKey | Out-Null
    Write-Ok "Connected."
}
catch {
    Write-Host ""
    Write-Warn "Could not authenticate to JIM: $($_.Exception.Message)"
    Write-Host ""
    Write-Host "  Most likely one of:" -ForegroundColor Yellow
    Write-Host "    - JIM.Web has not seeded this key yet. Restart it so the key in .env is picked up:" -ForegroundColor Gray
    Write-Host "        docker compose up -d --force-recreate jim.web" -ForegroundColor Gray
    Write-Host "      then re-run this script." -ForegroundColor Gray
    Write-Host "    - The key has expired (infrastructure keys last 24 hours). Re-run with -Force after" -ForegroundColor Gray
    Write-Host "      rotating it, or delete the JIM_INFRASTRUCTURE_API_KEY line from .env and re-run to" -ForegroundColor Gray
    Write-Host "      generate a fresh one." -ForegroundColor Gray
    Write-Host ""
    exit 1
}

Write-Host ""

# ─────────────────────────────────────────────────────────────────────────────
# Seed data definition
# A deterministic set that exercises the Trusted Certificate store's UI states.
# ─────────────────────────────────────────────────────────────────────────────
$now = [DateTimeOffset]::UtcNow
$seedCertificates = @(
    [PSCustomObject]@{
        Name       = "Contoso Root CA"
        Subject    = "CN=Contoso Root CA, O=Contoso, C=GB"
        NotBefore  = $now.AddDays(-1)
        NotAfter   = $now.AddYears(10)
        Notes      = "Test data: healthy long-lived root CA."
        Disable    = $false
    },
    [PSCustomObject]@{
        Name       = "Fabrikam Issuing CA"
        Subject    = "CN=Fabrikam Issuing CA, O=Fabrikam, C=GB"
        NotBefore  = $now.AddDays(-1)
        NotAfter   = $now.AddYears(2)
        Notes      = "Test data: healthy issuing CA, disabled to exercise the disabled state."
        Disable    = $true
    },
    [PSCustomObject]@{
        Name       = "Legacy LDAP Signing CA (expiring soon)"
        Subject    = "CN=Legacy LDAP Signing CA, O=Legacy Directory Services, C=GB"
        NotBefore  = $now.AddDays(-1)
        NotAfter   = $now.AddDays(21)
        Notes      = "Test data: valid but expiring soon, to exercise the expiry-warning state."
        Disable    = $false
    },
    [PSCustomObject]@{
        Name       = "Retired Directory CA (expired)"
        Subject    = "CN=Retired Directory CA, O=Legacy Directory Services, C=GB"
        NotBefore  = $now.AddDays(-400)
        NotAfter   = $now.AddDays(-30)
        Notes      = "Test data: already expired, to exercise the expired state and validation errors."
        Disable    = $false
    }
)

# Builds a self-signed CA certificate over the given validity window and returns its public
# DER bytes as Base64 (no private key: a Trusted Certificate stores the public certificate only).
function New-SelfSignedCertificateBase64 {
    param(
        [Parameter(Mandatory)][string]$Subject,
        [Parameter(Mandatory)][DateTimeOffset]$NotBefore,
        [Parameter(Mandatory)][DateTimeOffset]$NotAfter
    )

    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $Subject,
            $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

        # Mark it as a CA certificate, mirroring the kind of certificate an operator would trust.
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))

        $certificate = $request.CreateSelfSigned($NotBefore, $NotAfter)
        try {
            $der = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
            return [Convert]::ToBase64String($der)
        }
        finally {
            $certificate.Dispose()
        }
    }
    finally {
        $rsa.Dispose()
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Seed
# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Seeding Trusted Certificates"

# Snapshot existing certificate names once, for idempotency.
$existingNames = @(Get-JIMCertificate | ForEach-Object { $_.name })

$created = 0
$skipped = 0
$removed = 0

foreach ($seed in $seedCertificates) {
    $alreadyPresent = $existingNames -contains $seed.Name

    if ($alreadyPresent -and -not $Force) {
        Write-Info "Skipping '$($seed.Name)' (already present; use -Force to recreate)."
        $skipped++
        continue
    }

    if ($alreadyPresent -and $Force) {
        Get-JIMCertificate |
            Where-Object { $_.name -eq $seed.Name } |
            ForEach-Object {
                Remove-JIMCertificate -Id $_.id -Force | Out-Null
                $removed++
            }
    }

    $base64 = New-SelfSignedCertificateBase64 -Subject $seed.Subject -NotBefore $seed.NotBefore -NotAfter $seed.NotAfter

    $addedCert = Add-JIMCertificate -Name $seed.Name -CertificateBase64 $base64 `
        -Notes $seed.Notes -ChangeReason "Seeded by Seed-TrustedCertificates.ps1" -PassThru

    if ($seed.Disable -and $addedCert) {
        Set-JIMCertificate -Id $addedCert.id -Disable -ChangeReason "Seeded disabled by Seed-TrustedCertificates.ps1" | Out-Null
    }

    $state = if ($seed.Disable) { "created (disabled)" } else { "created" }
    Write-Ok "'$($seed.Name)' $state."
    $created++
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Step "Done"
Write-Info "Created: $created   Skipped: $skipped   Removed (during -Force): $removed"
Write-Info "View them at: $($Url.TrimEnd('/'))/admin/certificates"
Write-Host ""

exit 0
