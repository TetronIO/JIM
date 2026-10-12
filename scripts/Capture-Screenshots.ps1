# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Captures JIM's published screenshots from a running instance, at the exact size they are published.

.DESCRIPTION
    Every screenshot on the junctional.io product tour is listed in scripts/screenshots/manifest.json: the page, the
    steps that reach it, the themes it is published in and its geometry profile (#582). This script validates the
    manifest, signs in to the portal, captures each entry in each of its themes, and fails if any image is not exactly
    its profile's size, so a low-resolution image can never be published by accident. It also fails if any image is
    blank, or a dark and light pair came out in the same theme (ScreenshotChecks.ps1): a release publishes these with
    no one looking at them first.

    Portal images are 3348x2092: the portal at 1674x1046 CSS pixels and 100% zoom, rendered at 2x density. Navigation
    groups are expanded and the theme is set through the portal's own preferences before the first page loads.

    The instance should look as a customer sees it: every service running (Web, Worker, Scheduler), Production mode,
    and the data the published images show. Output goes to <OutputPath>/<Version>/ under the file names junctional.io
    uses in src/assets/tour/<version>/, so a refresh is a folder copy.

    Terminal images are 1944x1222 with a transparent background. Each runs its manifest commands for real against
    the portal, in a 120x30 pseudo-terminal, and redraws what the terminal showed in a macOS-style window: the
    output's own colours and layout, the typed command highlighted as PSReadLine highlights it, and only the last 30
    rows when the session runs longer. The session signs in with the API key at ApiKeyPath.

    Browser resolution is shared with the docs checks; see JimPlaywright.ps1.

.PARAMETER Version
    The folder the images are written to under OutputPath. Defaults to 'v' plus the VERSION file, matching the
    junctional.io asset folders (for example v0.17.0).

.PARAMETER OutputPath
    Where to write the images. Defaults to artifacts/screenshots, which is not committed.

.PARAMETER Name
    Capture only these manifest entries, for iterating on one page.

.PARAMETER BaseUrl
    The portal to capture. Defaults to http://localhost:5200.

.PARAMETER Credential
    The account to sign in with. Defaults to the bundled development realm's administrator.

.PARAMETER ManifestPath
    The manifest to read. Defaults to scripts/screenshots/manifest.json.

.PARAMETER PrepareData
    Before capturing, load the data the published images show and put the portal in the state a customer sees: run
    integration Scenario 8 (Cross-Domain Entitlement Sync) against OpenLDAP through Run-IntegrationTests.ps1, which
    resets the environment first and leaves it running; add the Scenario 8 Schedules; then rebuild the Web, Worker
    and Scheduler at the release version (the runner builds them with a development suffix) and recreate them, the
    portal in Production mode, on the same database. Replaces whatever the local stack held.

.PARAMETER ApiKeyPath
    The file holding the API key the terminal screenshots sign in with. Defaults to test/integration/.api-key, which
    Run-IntegrationTests.ps1 (and so -PrepareData) leaves behind. The key is read inside the recorded session only and
    never printed.

.PARAMETER Template
    The Scenario 8 data size for -PrepareData. Defaults to Scale100k50Groups, the size the published images show
    (100,001 users), which needs a host with 20+ GB of memory; use Small or Medium where that is not available.

.EXAMPLE
    pwsh ./scripts/Capture-Screenshots.ps1 -PrepareData
    Loads the published data, then captures every screenshot into artifacts/screenshots/v<VERSION>/.

.EXAMPLE
    pwsh ./scripts/Capture-Screenshots.ps1
    Captures every screenshot from the stack as it stands.

.EXAMPLE
    pwsh ./scripts/Capture-Screenshots.ps1 -Version dev -Name connected-systems-list
    Captures one page in both themes, for checking a change to it.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputPath,
    [string[]]$Name,
    [string]$BaseUrl = 'http://localhost:5200',
    [pscredential]$Credential,
    [string]$ManifestPath,
    [switch]$PrepareData,
    [string]$Template = 'Scale100k50Groups',
    [string]$ApiKeyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'screenshots' 'ScreenshotManifest.ps1')
. (Join-Path $PSScriptRoot 'screenshots' 'CapturePreflight.ps1')
. (Join-Path $PSScriptRoot 'screenshots' 'TerminalScreen.ps1')
. (Join-Path $PSScriptRoot 'screenshots' 'ScreenshotChecks.ps1')
. (Join-Path $PSScriptRoot 'JimPlaywright.ps1')

if (-not $ManifestPath) { $ManifestPath = Join-Path $PSScriptRoot 'screenshots' 'manifest.json' }
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'artifacts' 'screenshots' }
if (-not $Version) { $Version = 'v' + (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim() }
if (-not $ApiKeyPath) { $ApiKeyPath = Join-Path $repoRoot 'test' 'integration' '.api-key' }
if ($Version -notmatch '^[A-Za-z0-9._-]+$') { throw "Version must be a folder name, got '$Version'." }
if (-not $Credential) {
    # The bundled development realm's documented administrator (see README and .devcontainer/CLAUDE.md).
    $Credential = [pscredential]::new('admin', (ConvertTo-SecureString 'admin' -AsPlainText -Force))
}

$manifest = Read-ScreenshotManifest -Path $ManifestPath

if ($PrepareData) {
    $integration = Join-Path $repoRoot 'test' 'integration'
    $dataStarted = Get-Date

    Write-Host "Loading Scenario 8 at $Template against OpenLDAP (resets the local stack) ..."
    & (Join-Path $integration 'Run-IntegrationTests.ps1') -Scenario 8 -Template $Template -DirectoryType OpenLDAP
    if ($LASTEXITCODE -ne 0) { throw "Scenario 8 did not complete (exit $LASTEXITCODE); the data is not what the screenshots should show." }

    # The runner leaves the key it minted here; it is read, never printed.
    $apiKey = (Get-Content -LiteralPath (Join-Path $integration '.api-key') -Raw).Trim()
    Write-Host 'Adding the Scenario 8 Schedules ...'
    $global:LASTEXITCODE = 0
    & (Join-Path $integration 'Add-Scenario-008-Schedules.ps1') -JIMUrl $BaseUrl -ApiKey $apiKey
    if (-not $? -or $LASTEXITCODE -ne 0) { throw 'Adding the Scenario 8 Schedules failed.' }

    # The runner builds with a development version suffix (v0.16.0-dev.20261011.919), which the portal's footer and
    # Connect-JIM would both show. Rebuild all three services without it: rebuilding the web alone would leave the
    # Worker and Scheduler on the suffixed version, and Service Health would mark them as differing from the portal.
    $services = @('jim.web', 'jim.worker', 'jim.scheduler')
    $compose = @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml', '-f', 'scripts/screenshots/docker-compose.production-web.yml')
    Remove-Item Env:VERSION_SUFFIX -ErrorAction SilentlyContinue
    Push-Location $repoRoot
    try {
        Write-Host 'Rebuilding the JIM services at the release version ...'
        docker compose @compose build @services | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Rebuilding the JIM services failed (docker compose exited $LASTEXITCODE)." }
        Write-Host 'Recreating them, with the portal in Production mode ...'
        docker compose @compose up -d --no-deps @services | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Recreating the JIM services failed (docker compose exited $LASTEXITCODE)." }
    }
    finally { Pop-Location }

    $deadline = (Get-Date).AddMinutes(5)
    do {
        Start-Sleep -Seconds 3
        try { $ready = (Invoke-WebRequest -Uri "$BaseUrl/api/v1/health/ready" -SkipHttpErrorCheck -TimeoutSec 10).StatusCode -eq 200 }
        catch [System.Net.Http.HttpRequestException] { $ready = $false }
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { throw "The portal did not report ready within 5 minutes of restarting in Production mode." }

    # Sign-in needs Keycloak where the realm sends browsers. The devcontainer forwards that address and the runner
    # bridges it where socat is installed; on a host with neither (a CI runner), publish it on loopback instead.
    $signInOrigin = 'http://localhost:8181'
    if (-not (Test-HttpOriginAnswers -Origin $signInOrigin)) {
        Write-Host "Publishing Keycloak at $signInOrigin (loopback only) for sign-in ..."
        Push-Location $repoRoot
        try {
            docker compose @compose -f 'scripts/screenshots/docker-compose.keycloak-loopback.yml' up -d --no-deps jim.keycloak | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Publishing Keycloak at $signInOrigin failed (docker compose exited $LASTEXITCODE)." }
        }
        finally { Pop-Location }
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep -Seconds 3 } until ((Test-HttpOriginAnswers -Origin $signInOrigin) -or (Get-Date) -gt $deadline)
        if (-not (Test-HttpOriginAnswers -Origin $signInOrigin)) { throw "Keycloak did not answer at $signInOrigin within 3 minutes of publishing it." }
    }
    Write-Host ("Data ready in {0:hh\:mm\:ss}." -f ((Get-Date) - $dataStarted))
}

$screenshots = @($manifest.Screenshots)
if ($Name) {
    # From bash, `pwsh -File ... -Name a,b` arrives as the one string 'a,b'.
    $Name = @($Name | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $unknown = @($Name | Where-Object { $_ -notin $screenshots.name })
    if ($unknown.Count -gt 0) { throw "Not in the manifest: $($unknown -join ', ')." }
    $screenshots = @($screenshots | Where-Object { $_.name -in $Name })
}

# The terminal a screenshot's session runs in, and the prompt and size a published image shows.
$terminalPrompt = 'PS /Users/jim> '
$terminalColumns = 120
$terminalRows = 30

# Records one terminal screenshot's commands in a pseudo-terminal and works out the screen it ends on.
function Get-TerminalScreenshotScreen {
    param([Parameter(Mandatory)]$Screenshot, [Parameter(Mandatory)][string]$WorkPath)

    $commands = @($Screenshot.commands | Where-Object { $_.Trim() -ne '' } | ForEach-Object { Resolve-TerminalCommand -Command $_ -BaseUrl $BaseUrl })
    $shown = @($commands | Where-Object { -not $_.StartsWith('!') })
    $commandsFile = Join-Path $WorkPath "$($Screenshot.name).commands.txt"
    $transcriptPath = Join-Path $WorkPath "$($Screenshot.name).transcript"
    Set-Content -LiteralPath $commandsFile -Value $commands -Encoding utf8NoBOM

    $session = Join-Path $PSScriptRoot 'screenshots' 'terminal-session.ps1'
    $module = Join-Path $repoRoot 'src' 'JIM.PowerShell' 'JIM.psd1'
    foreach ($path in $session, $commandsFile, $ApiKeyPath, $module) {
        if ($path.Contains("'")) { throw "Cannot run a terminal session from a path containing a quote: $path" }
    }
    $inner = "export TERM=xterm-256color LC_ALL=C.UTF-8 POWERSHELL_UPDATECHECK=Off; stty cols $terminalColumns rows $terminalRows; " +
        "exec pwsh -NoLogo -NoProfile -File '$session' -CommandsFile '$commandsFile' -KeyFile '$ApiKeyPath' -ModulePath '$module'"
    & script --quiet --return --command $inner $transcriptPath | Out-Null

    $transcript = if (Test-Path -LiteralPath $transcriptPath) { [System.IO.File]::ReadAllText($transcriptPath, [System.Text.Encoding]::UTF8) } else { '' }
    Remove-Item -LiteralPath $transcriptPath -Force -ErrorAction SilentlyContinue
    $outputs = @(Split-TerminalTranscript -Transcript $transcript -Count $shown.Count)
    New-TerminalScreen -Commands $shown -Outputs $outputs -Prompt $terminalPrompt -Columns $terminalColumns -Rows $terminalRows
}

$portalScreenshots = @($screenshots | Where-Object kind -eq 'portal')
$terminalScreenshots = @($screenshots | Where-Object kind -eq 'terminal')
if ($portalScreenshots.Count + $terminalScreenshots.Count -eq 0) { throw 'Nothing to capture.' }

if ($terminalScreenshots.Count -gt 0) {
    if (-not (Get-Command script -ErrorAction SilentlyContinue)) {
        throw "Terminal screenshots record their session with 'script' (util-linux), which is not installed here."
    }
    if (-not (Test-Path -LiteralPath $ApiKeyPath)) {
        throw "Terminal screenshots sign in with the API key in $ApiKeyPath, which does not exist; run with -PrepareData, or pass -ApiKeyPath."
    }
}

# Every portal capture signs in first, so an identity provider that is not answering fails every image the same way.
if ($portalScreenshots.Count -gt 0) {
    $signInProblem = Get-ScreenshotSignInProblem -BaseUrl $BaseUrl
    if ($signInProblem) { throw $signInProblem }
}

$versionPath = Join-Path $OutputPath $Version
New-Item -ItemType Directory -Path $versionPath -Force | Out-Null
$versionPath = (Resolve-Path -LiteralPath $versionPath).Path
$failedPath = Join-Path $versionPath '_failed'
if (Test-Path -LiteralPath $failedPath) { Remove-Item -LiteralPath $failedPath -Recurse -Force }

$work = Join-Path ([System.IO.Path]::GetTempPath()) "jim-screenshots-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path $work | Out-Null
$configPath = Join-Path $work 'run.json'
$resultsPath = Join-Path $work 'results.json'
$started = Get-Date
$results = [System.Collections.Generic.List[object]]::new()

try {
    $jobs = [System.Collections.Generic.List[object]]::new()
    foreach ($screenshot in $portalScreenshots) {
        foreach ($file in Get-ScreenshotFileNames -Screenshot $screenshot) {
            $jobs.Add([pscustomobject]@{
                kind     = 'portal'
                name     = $screenshot.name
                theme    = $file.Theme
                fileName = $file.FileName
                profile  = $screenshot.profile
                steps    = @($screenshot.steps)
            })
        }
    }

    if ($terminalScreenshots.Count -gt 0) { Write-Host "Recording $($terminalScreenshots.Count) terminal session(s) ..." }
    foreach ($screenshot in $terminalScreenshots) {
        try { $screen = @(Get-TerminalScreenshotScreen -Screenshot $screenshot -WorkPath $work) }
        catch {
            # One failed session fails its own images; the rest of the run goes on.
            foreach ($file in Get-ScreenshotFileNames -Screenshot $screenshot) {
                $jobs.Add([pscustomobject]@{ kind = 'terminal'; name = $screenshot.name; theme = $file.Theme; fileName = $file.FileName; profile = $screenshot.profile; screen = $null })
                $results.Add([pscustomobject]@{ fileName = $file.FileName; ok = $false; error = $_.Exception.Message })
            }
            continue
        }
        foreach ($file in Get-ScreenshotFileNames -Screenshot $screenshot) {
            $jobs.Add([pscustomobject]@{ kind = 'terminal'; name = $screenshot.name; theme = $file.Theme; fileName = $file.FileName; profile = $screenshot.profile; screen = $screen })
        }
    }

    $failedEarly = @($results | ForEach-Object { $_.fileName })
    $driverJobs = @($jobs | Where-Object { $_.fileName -notin $failedEarly })
    [pscustomobject]@{
        baseUrl   = $BaseUrl
        outputDir = $versionPath
        fontsDir  = Join-Path $repoRoot 'src' 'JIM.Web' 'wwwroot' 'fonts'
        profiles  = $manifest.Profiles
        jobs      = $driverJobs
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM

    if ($driverJobs.Count -gt 0) {
        Write-Host "Capturing $($driverJobs.Count) image(s) from $BaseUrl into $versionPath ..."
        # The account travels in the child's environment only, never on its command line or on disk.
        $env:JIM_PLAYWRIGHT_MODULE = Resolve-PlaywrightModule
        $env:JIM_CAPTURE_USERNAME = $Credential.UserName
        $env:JIM_CAPTURE_PASSWORD = $Credential.GetNetworkCredential().Password
        & node (Join-Path $PSScriptRoot 'screenshots' 'capture.cjs') $configPath $resultsPath
        if ($LASTEXITCODE -ne 0) { throw "The capture driver failed (node exited $LASTEXITCODE)." }
        foreach ($result in @(Get-Content -LiteralPath $resultsPath -Raw | ConvertFrom-Json)) { $results.Add($result) }
    }
}
finally {
    Remove-Item Env:JIM_PLAYWRIGHT_MODULE, Env:JIM_CAPTURE_USERNAME, Env:JIM_CAPTURE_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# Every image must be exactly its profile's size: the guard against publishing a low-resolution capture.
$problems = [System.Collections.Generic.List[string]]::new()
foreach ($result in $results) {
    $job = $jobs | Where-Object fileName -eq $result.fileName | Select-Object -First 1
    if (-not $result.ok) {
        $problems.Add("$($result.fileName): $($result.error)")
        continue
    }
    $mismatch = Find-ScreenshotSizeMismatch -Path (Join-Path $versionPath $result.fileName) -ProfileName $job.profile -Profiles $manifest.Profiles
    if ($mismatch) { $problems.Add($mismatch) }
}
$reported = @($results | ForEach-Object { $_.fileName })
foreach ($job in $jobs | Where-Object { $_.fileName -notin $reported }) {
    $problems.Add("$($job.fileName): no result from the capture driver.")
}

# Nothing blank, and every dark and light pair in its own theme. A release publishes these with no one looking first.
$writtenPaths = @($results | Where-Object ok | ForEach-Object { Join-Path $versionPath $_.fileName } |
        Where-Object { Test-Path -LiteralPath $_ })
if ($writtenPaths.Count -gt 0) {
    foreach ($problem in @(Find-ScreenshotContentProblem -Path $writtenPaths)) { $problems.Add($problem) }
}

$elapsed = (Get-Date) - $started
$written = @($results | Where-Object ok).Count
Write-Host ''
Write-Host ("Screenshots: {0} of {1} written in {2:mm\:ss}" -f $written, $jobs.Count, $elapsed)
foreach ($group in $jobs | Group-Object profile) {
    $sizeProfile = $manifest.Profiles[$group.Name]
    $themes = ($group.Group | Group-Object theme | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '
    Write-Host ("  {0,-14} {1}x{2}  {3}" -f $group.Name, $sizeProfile.ImageWidth, $sizeProfile.ImageHeight, $themes)
}

if ($problems.Count -gt 0) {
    Write-Host ''
    Write-Host "$($problems.Count) problem(s); nothing here is ready to publish until they are fixed:" -ForegroundColor Red
    foreach ($problem in $problems) { Write-Host "  $problem" -ForegroundColor Red }
    if (Test-Path -LiteralPath $failedPath) { Write-Host "  What each failed page looked like: $failedPath" -ForegroundColor Red }
    exit 1
}

Write-Host "All images are at their published size, none blank, and each pair in its own theme. Output: $versionPath" -ForegroundColor Green
