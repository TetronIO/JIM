# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Provisions the GitHub repo secrets and variable required for JIM-Bench
    ingestion of integration test metrics.

.DESCRIPTION
    Sets up the three pieces the #476 metrics pipeline needs on the JIM repo:

      - JIM_BENCH_API_URL       (Actions variable)  ingestion base URL
      - JIM_BENCH_API_KEY       (secret)            X-API-Key for ingestion
      - JIM_BENCH_DISPATCH_TOKEN (secret)           PAT used by bench-sync.yml
                                                    to dispatch into
                                                    TetronIO/JIM-Bench

    The script is idempotent: if a value is already set, it is listed and
    you are asked whether to overwrite. Each of the three can be skipped
    individually.

    Secret values are read with Read-Host -AsSecureString so they do not
    echo to the terminal and do not appear in shell history. Values are
    handed to gh via stdin, not via --body, so they do not appear in the
    process list.

    Requires the GitHub CLI (gh) to be installed and authenticated with
    admin rights on the target repository.

.PARAMETER Repository
    Target repo in OWNER/NAME form. Defaults to TetronIO/JIM.

.PARAMETER ApiUrl
    Optional non-interactive value for JIM_BENCH_API_URL. If omitted,
    prompts with the default https://bench-api.junctional.io.

.EXAMPLE
    ./scripts/Set-JIMBenchSecrets.ps1

.EXAMPLE
    ./scripts/Set-JIMBenchSecrets.ps1 -Repository TetronIO/JIM -ApiUrl https://bench-api.junctional.io
#>

[CmdletBinding()]
param(
    [string]$Repository = "TetronIO/JIM",
    [string]$ApiUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-GhAvailable {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) is not installed or not on PATH. Install from https://cli.github.com/ and re-run."
    }
    $authStatus = gh auth status 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is not authenticated. Run 'gh auth login' and re-run. Output: $authStatus"
    }
}

function Test-RepoAccess {
    param([string]$Repo)
    gh repo view $Repo --json name 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot access repository '$Repo'. Check the name and your permissions."
    }
}

function Get-ExistingSecretNames {
    param([string]$Repo)
    $output = gh secret list --repo $Repo --json name 2>&1
    if ($LASTEXITCODE -ne 0) { return @() }
    return ($output | ConvertFrom-Json).name
}

function Get-ExistingVariableNames {
    param([string]$Repo)
    $output = gh variable list --repo $Repo --json name 2>&1
    if ($LASTEXITCODE -ne 0) { return @() }
    return ($output | ConvertFrom-Json).name
}

function Confirm-Overwrite {
    param(
        [string]$Name,
        [string]$Kind
    )
    $response = Read-Host "$Kind '$Name' already exists. Overwrite? [y/N]"
    return ($response -match '^(y|yes)$')
}

function Set-GhVariable {
    param(
        [string]$Repo,
        [string]$Name,
        [string]$Value
    )
    # gh variable set reads from --body; value is non-sensitive so CLI-arg is fine
    gh variable set $Name --repo $Repo --body $Value
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set variable '$Name'."
    }
    Write-Host "  Variable '$Name' set." -ForegroundColor Green
}

function Set-GhSecretFromSecureString {
    param(
        [string]$Repo,
        [string]$Name,
        [System.Security.SecureString]$SecureValue
    )
    # Convert SecureString to plaintext just long enough to pipe to gh.
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        # Pipe via stdin so the secret never appears in argv.
        $plain | gh secret set $Name --repo $Repo
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set secret '$Name'."
        }
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    Write-Host "  Secret '$Name' set." -ForegroundColor Green
}

Test-GhAvailable
Test-RepoAccess -Repo $Repository

Write-Host ""
Write-Host "Provisioning JIM-Bench secrets on '$Repository'" -ForegroundColor Cyan
Write-Host ""

$existingSecrets = Get-ExistingSecretNames -Repo $Repository
$existingVariables = Get-ExistingVariableNames -Repo $Repository

# 1) JIM_BENCH_API_URL (variable)
$varName = "JIM_BENCH_API_URL"
Write-Host "1/3 $varName (Actions variable)" -ForegroundColor White
if ($existingVariables -contains $varName) {
    $current = (gh variable get $varName --repo $Repository 2>$null)
    Write-Host "    Current value: $current" -ForegroundColor Gray
    if (-not (Confirm-Overwrite -Name $varName -Kind "Variable")) {
        Write-Host "    Skipped." -ForegroundColor Yellow
        $varName = $null
    }
}
if ($varName) {
    if (-not $ApiUrl) {
        $default = "https://bench-api.junctional.io"
        $entered = Read-Host "    URL [$default]"
        $ApiUrl = if ([string]::IsNullOrWhiteSpace($entered)) { $default } else { $entered.Trim() }
    }
    Set-GhVariable -Repo $Repository -Name $varName -Value $ApiUrl
}
Write-Host ""

# 2) JIM_BENCH_API_KEY (secret)
$secretName = "JIM_BENCH_API_KEY"
Write-Host "2/3 $secretName (secret)" -ForegroundColor White
$proceed = $true
if ($existingSecrets -contains $secretName) {
    $proceed = Confirm-Overwrite -Name $secretName -Kind "Secret"
    if (-not $proceed) { Write-Host "    Skipped." -ForegroundColor Yellow }
}
if ($proceed) {
    $secure = Read-Host "    API key (input hidden)" -AsSecureString
    if ($secure.Length -eq 0) {
        Write-Host "    Empty input; skipped." -ForegroundColor Yellow
    }
    else {
        Set-GhSecretFromSecureString -Repo $Repository -Name $secretName -SecureValue $secure
    }
}
Write-Host ""

# 3) JIM_BENCH_DISPATCH_TOKEN (secret, optional)
$secretName = "JIM_BENCH_DISPATCH_TOKEN"
Write-Host "3/3 $secretName (secret, optional)" -ForegroundColor White
Write-Host "    GitHub PAT with 'repo' scope on TetronIO/JIM-Bench, used by" -ForegroundColor Gray
Write-Host "    .github/workflows/bench-sync.yml to dispatch contract-change events." -ForegroundColor Gray
Write-Host "    Omitting this makes bench-sync.yml skip with a warning rather than fail." -ForegroundColor Gray
$proceed = $true
if ($existingSecrets -contains $secretName) {
    $proceed = Confirm-Overwrite -Name $secretName -Kind "Secret"
    if (-not $proceed) { Write-Host "    Skipped." -ForegroundColor Yellow }
}
if ($proceed) {
    $secure = Read-Host "    PAT (input hidden, press Enter to skip)" -AsSecureString
    if ($secure.Length -eq 0) {
        Write-Host "    Empty input; skipped." -ForegroundColor Yellow
    }
    else {
        Set-GhSecretFromSecureString -Repo $Repository -Name $secretName -SecureValue $secure
    }
}
Write-Host ""

Write-Host "Done. Current state:" -ForegroundColor Cyan
gh variable list --repo $Repository
gh secret list --repo $Repository
