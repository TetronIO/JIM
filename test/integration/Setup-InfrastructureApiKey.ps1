<#
.SYNOPSIS
    Set up an infrastructure API key for automated testing

.DESCRIPTION
    Creates or retrieves an infrastructure API key by setting the JIM_INFRASTRUCTURE_API_KEY
    environment variable and restarting JIM.Web. The key is then exported to the environment
    for use by integration test scripts.

.PARAMETER KeyValue
    Optional specific key value to use. If not provided, generates a secure random key.

.EXAMPLE
    ./Setup-InfrastructureApiKey.ps1

.EXAMPLE
    ./Setup-InfrastructureApiKey.ps1 -KeyValue "jim_ak_test_infrastructure_key_12345678"
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$KeyValue
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Infrastructure API Key Setup" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Generate a secure random key if not provided
if (-not $KeyValue) {
    Write-Host "Generating secure random API key..." -ForegroundColor Gray

    # Generate 32 random bytes and convert to base64
    $randomBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($randomBytes)
    $randomString = [Convert]::ToBase64String($randomBytes).Replace("+", "").Replace("/", "").Replace("=", "")

    $KeyValue = "jim_ak_$randomString"
    Write-Host "  ✓ Generated key prefix: $($KeyValue.Substring(0, [Math]::Min(12, $KeyValue.Length)))" -ForegroundColor Green
}
else {
    Write-Host "Using provided API key..." -ForegroundColor Gray

    if ($KeyValue.Length -lt 32) {
        Write-Host "  ✗ Key too short (minimum 32 characters required)" -ForegroundColor Red
        exit 1
    }

    if (-not $KeyValue.StartsWith("jim_ak_")) {
        Write-Host "  ⚠ Warning: Key should start with 'jim_ak_' by convention" -ForegroundColor Yellow
    }

    Write-Host "  ✓ Key validation passed" -ForegroundColor Green
}

# Update .env file with the API key (this file is gitignored)
Write-Host ""
Write-Host "Updating .env file with API key..." -ForegroundColor Gray

$envFilePath = "/workspaces/JIM/.env"

if (-not (Test-Path $envFilePath)) {
    Write-Host "  ⚠ .env file not found, creating from .env.example..." -ForegroundColor Yellow
    if (Test-Path "/workspaces/JIM/.env.example") {
        Copy-Item "/workspaces/JIM/.env.example" $envFilePath
    }
    else {
        # Create minimal .env file
        "" | Out-File $envFilePath -Encoding UTF8
    }
}

$envContent = Get-Content $envFilePath -Raw
if ($null -eq $envContent) { $envContent = "" }

# Check if JIM_INFRASTRUCTURE_API_KEY is already in the file
if ($envContent -match "JIM_INFRASTRUCTURE_API_KEY=") {
    Write-Host "  JIM_INFRASTRUCTURE_API_KEY already present in .env" -ForegroundColor Yellow
    Write-Host "  Updating value..." -ForegroundColor Gray

    # Replace the existing value
    $envContent = $envContent -replace "JIM_INFRASTRUCTURE_API_KEY=.*", "JIM_INFRASTRUCTURE_API_KEY=$KeyValue"
    $envContent | Set-Content $envFilePath -NoNewline

    Write-Host "  ✓ Updated JIM_INFRASTRUCTURE_API_KEY in .env" -ForegroundColor Green
}
else {
    Write-Host "  Adding JIM_INFRASTRUCTURE_API_KEY to .env..." -ForegroundColor Gray

    # Append the key to the file
    $newLine = if ($envContent.EndsWith("`n")) { "" } else { "`n" }
    $envContent = $envContent + $newLine + "JIM_INFRASTRUCTURE_API_KEY=$KeyValue`n"
    $envContent | Set-Content $envFilePath -NoNewline

    Write-Host "  ✓ Added JIM_INFRASTRUCTURE_API_KEY to .env" -ForegroundColor Green
}

# Recreate jim.web to pick up the new environment variable
# Note: 'restart' just restarts the existing container (with old env vars)
#       'up --force-recreate' creates a new container with updated env vars
Write-Host ""
Write-Host "Recreating JIM.Web to apply changes..." -ForegroundColor Gray

$composeOverridePath = "/workspaces/JIM/docker-compose.override.yml"
docker compose -f /workspaces/JIM/docker-compose.yml -f $composeOverridePath --profile with-db up -d --force-recreate jim.web 2>&1 | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ Failed to recreate jim.web" -ForegroundColor Red
    exit 1
}

Write-Host "  ✓ JIM.Web recreated" -ForegroundColor Green

# Wait for JIM to be ready
Write-Host ""
Write-Host "Waiting for JIM.Web to be ready (checking http://localhost:5200)..." -ForegroundColor Gray

$maxAttempts = 30
$attempt = 0
$jimReady = $false

while ($attempt -lt $maxAttempts -and -not $jimReady) {
    $attempt++

    try {
        $response = Invoke-WebRequest -Uri "http://localhost:5200" -Method GET -TimeoutSec 2 -ErrorAction SilentlyContinue
        if ($response.StatusCode -in @(200, 302)) {
            $jimReady = $true
        }
    }
    catch {
        # Ignore errors, keep trying
    }

    if (-not $jimReady) {
        Write-Host "  Attempt $attempt/$maxAttempts - waiting..." -ForegroundColor Gray
        Start-Sleep -Seconds 2
    }
}

if (-not $jimReady) {
    Write-Host "  ✗ JIM.Web did not become ready within timeout" -ForegroundColor Red
    Write-Host "  Check docker logs: docker logs jim.web" -ForegroundColor Yellow
    exit 1
}

Write-Host "  ✓ JIM.Web is ready" -ForegroundColor Green

# Export the key to environment for current session
Write-Host ""
Write-Host "Exporting JIM_API_KEY to environment..." -ForegroundColor Gray

$env:JIM_API_KEY = $KeyValue

# Also write to a file so parent scripts can read it
$keyFilePath = "$PSScriptRoot/.api-key"
$KeyValue | Out-File -FilePath $keyFilePath -NoNewline -Encoding UTF8

Write-Host "  ✓ JIM_API_KEY exported" -ForegroundColor Green

# Summary
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Setup Complete" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "Infrastructure API Key configured successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Key Details:" -ForegroundColor Cyan
Write-Host "  Prefix:      $($KeyValue.Substring(0, [Math]::Min(12, $KeyValue.Length)))" -ForegroundColor White
Write-Host "  Expiry:      24 hours from JIM.Web startup" -ForegroundColor White
Write-Host "  Roles:       Administrator" -ForegroundColor White
Write-Host "  Saved to:    test/integration/.api-key" -ForegroundColor White
Write-Host ""
Write-Host "You can now run integration tests:" -ForegroundColor Cyan
Write-Host "  cd test/integration" -ForegroundColor Gray
Write-Host "  ./Invoke-IntegrationTests.ps1 -Template Small -Phase 1" -ForegroundColor Gray
Write-Host ""
Write-Host "Or run specific scenarios:" -ForegroundColor Cyan
Write-Host "  pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Template Nano -ApiKey (Get-Content test/integration/.api-key)" -ForegroundColor Gray
Write-Host ""

exit 0
