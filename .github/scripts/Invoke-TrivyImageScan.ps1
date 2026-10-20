# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Scans a locally built JIM image with the pinned Trivy scanner and fails on fixable HIGH or CRITICAL findings.

.DESCRIPTION
    The one image scan JIM runs: CI's scan-images job runs it on every push and pull request, and the release
    workflow runs it on each image before that image is pushed. Until #2075 the two scanned differently (CI with a
    pinned scanner and a CVSS gate, the release with whichever Trivy version trivy-action brought and Trivy's own
    severity labels), so a release could fail on a finding CI had passed, or ship one CI would have blocked.

    1. The scanner is the image pinned by tag and digest in .github/trivy-image.txt, which tooling-pin-check
       watches. Anything that is not a digest pin is refused before any scan runs.
    2. The vulnerability database is downloaded as its own phase, retried, before the scan reads it with
       --skip-db-update. Trivy's built-in repository fallback does not cover every failure: on run 36450669448
       mirror.gcr.io answered the manifest but 404'd a layer, and Trivy exited without trying the next repository.
       GHCR is listed first because GITHUB_TOKEN, when set, authenticates it (no anonymous rate limit).
    3. Every finding is written to a SARIF file, with .trivyignore applied and findings that have no published fix
       left out (nothing can be done about those).
    4. The gate reads the CVSS base score Trivy records on each SARIF rule ('security-severity', the field GitHub
       Code Scanning classifies alerts by) and fails on any finding scoring 7.0 or more.

    Run it from the repository root: the root is mounted into the scanner, so .trivyignore and the SARIF output
    resolve against it. Requires Docker, and the image to scan in the local Docker image store.

.PARAMETER ImageRef
    The image to scan, already in the local Docker image store (for example `jim-web:scan`).

.PARAMETER SarifPath
    Where to write the SARIF, relative to the repository root. CI uploads it to code scanning.

.EXAMPLE
    pwsh ./.github/scripts/Invoke-TrivyImageScan.ps1 -ImageRef jim-web:scan

.NOTES
    Exit codes: 0 no blocking finding; 1 a blocking finding, or the scan could not be completed (an unpinned
    scanner, a database that would not download, no SARIF written). A scan that could not run is never reported as
    a clean image.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ImageRef,
    [string]$SarifPath = 'trivy-results.sarif'
)

$ErrorActionPreference = 'Stop'

$workspace = (Get-Location).Path
$pinFile = Join-Path $workspace '.github/trivy-image.txt'
$databaseRepositories = 'ghcr.io/aquasecurity/trivy-db:2,mirror.gcr.io/aquasec/trivy-db:2'
$downloadAttempts = 3

# --- The scanner -------------------------------------------------------------

$trivyImage = if (Test-Path $pinFile) {
    @(Get-Content $pinFile | Where-Object { $_ -notmatch '^\s*(#|$)' }) -join "`n"
}
if ($trivyImage -notmatch '^aquasec/trivy:\d[\d.]*@sha256:[0-9a-f]{64}$') {
    Write-Host "::error title=Trivy image not pinned::.github/trivy-image.txt must hold one digest-pinned aquasec/trivy reference; found '$trivyImage'."
    exit 1
}
Write-Host "Scanner: $trivyImage"

$cacheDirectory = Join-Path ($env:RUNNER_TEMP ? $env:RUNNER_TEMP : [System.IO.Path]::GetTempPath()) 'trivy-cache'
New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
$tokenArguments = if ($env:GITHUB_TOKEN) { @('-e', 'GITHUB_TOKEN') } else { @() }

# --- The vulnerability database ----------------------------------------------

for ($attempt = 1; ; $attempt++) {
    # As the caller's own user, so a CI runner can empty RUNNER_TEMP afterwards.
    docker run --rm --user "$(id -u):$(id -g)" -v "${cacheDirectory}:/trivy-cache" @tokenArguments $trivyImage image `
        --cache-dir /trivy-cache --db-repository $databaseRepositories --no-progress --download-db-only
    if ($LASTEXITCODE -eq 0) { break }
    if ($attempt -eq $downloadAttempts) {
        Write-Host "::error title=Trivy DB download failed::Could not download the vulnerability DB after $attempt attempts."
        exit 1
    }
    Write-Host "::warning title=Trivy DB download failed::Attempt $attempt failed; retrying in $($attempt * 15)s."
    Start-Sleep -Seconds ($attempt * 15)
}

# --- The scan ----------------------------------------------------------------

$sarifFile = Join-Path $workspace $SarifPath
Remove-Item -Path $sarifFile -ErrorAction SilentlyContinue
# --exit-code 0: the gate below decides, from the SARIF.
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v "${workspace}:/workspace" -v "${cacheDirectory}:/trivy-cache" `
    -w /workspace -e TRIVY_IGNORE_UNFIXED=true $trivyImage image `
    --cache-dir /trivy-cache --cache-backend memory --skip-db-update --scanners vuln `
    --format sarif --output $SarifPath --ignorefile .trivyignore --image-src docker --exit-code 0 `
    $ImageRef
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $sarifFile)) {
    Write-Host "::error title=Trivy scan failed::Trivy did not produce a SARIF file for $ImageRef."
    exit 1
}

# --- The gate ----------------------------------------------------------------

$sarif = Get-Content $sarifFile -Raw | ConvertFrom-Json

$scoreByRule = @{}
foreach ($run in @($sarif.runs)) {
    foreach ($rule in @($run.tool.driver.rules)) {
        if (-not $rule) { continue }
        $score = 0.0
        $raw = $rule.properties.'security-severity'
        if ($raw) { [void][double]::TryParse($raw, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$score) }
        $scoreByRule[$rule.id] = $score
    }
}

$counts = [ordered]@{ CRITICAL = 0; HIGH = 0; MEDIUM = 0; LOW = 0 }
$blocking = @()
foreach ($run in @($sarif.runs)) {
    foreach ($result in @($run.results)) {
        if (-not $result) { continue }
        $score = [double]$scoreByRule[$result.ruleId]
        $band = if ($score -ge 9.0) { 'CRITICAL' } elseif ($score -ge 7.0) { 'HIGH' } elseif ($score -ge 4.0) { 'MEDIUM' } else { 'LOW' }
        $counts[$band]++
        if ($score -ge 7.0) { $blocking += "$($result.ruleId) (CVSS $score)" }
    }
}

Write-Host "Trivy findings for ${ImageRef}:"
Write-Host "  CRITICAL: $($counts.CRITICAL)"
Write-Host "  HIGH:     $($counts.HIGH)"
Write-Host "  MEDIUM:   $($counts.MEDIUM)  (not blocking)"
Write-Host "  LOW:      $($counts.LOW)  (not blocking)"
Write-Host ''

if ($blocking.Count -gt 0) {
    Write-Host 'Blocking CVEs (CVSS >= 7.0):'
    $blocking | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    Write-Host 'Fixable CRITICAL/HIGH vulnerabilities found. Failing.'
    Write-Host 'See the GitHub Security tab for details:'
    Write-Host '  https://github.com/TetronIO/JIM/security/code-scanning'
    exit 1
}
Write-Host 'No fixable CRITICAL/HIGH vulnerabilities found.'
exit 0
