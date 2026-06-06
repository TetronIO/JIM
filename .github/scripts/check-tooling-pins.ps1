# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Detects when newer versions of the manually-pinned development tooling (npm
    and NuGet packages installed outside any manifest Dependabot can read) are
    available, and (optionally) rewrites the pins in place so a bump can be
    proposed for evaluation.

.DESCRIPTION
    A few development tools are pinned to exact versions in places Dependabot does
    not look:

      - `@playwright/mcp` (the Playwright MCP server used for UI validation) is
        pinned in BOTH `.devcontainer/setup.sh` (the install-at-create step) and
        `.mcp.json` (the launch-at-runtime config). The two MUST move together or
        the installed browser and the server drift apart.
      - `dotnet-ef` (the EF Core CLI) is pinned in `.devcontainer/setup.sh`.

    Dependabot only reads real manifests (`package.json`, `.csproj`, Dockerfile
    FROM lines, workflow `uses:`). None of these pins live in one, so without this
    check a newer release sits unnoticed until someone happens to look.

    This script closes that gap:

      1. For each tool, reads the currently-pinned version from its file(s).
      2. Queries the upstream registry (npm or NuGet) for the latest stable
         version.
      3. Flags any tool whose pinned version is behind the latest.

    Default mode reports findings as a table. `-Apply` additionally rewrites the
    bumps into the pin files in place (used by the tooling-pin-check workflow to
    produce a PR for evaluation). When a tool is pinned in more than one file,
    every location is rewritten to the same new version, healing any drift.

    Runnable locally from the repository root for ad hoc checks:

        pwsh -NoProfile -File .github/scripts/check-tooling-pins.ps1

    Requires outbound HTTPS to registry.npmjs.org and api.nuget.org. Unlike the
    apt pin check it needs no Docker; these are registry HTTP lookups.

.PARAMETER Apply
    Rewrite the available bumps into the pin files in place. Without it, the
    script only reports.

.NOTES
    Exit codes:
      0  success; no updates available (or -Apply applied them cleanly)
      1  an error occurred (a registry query failed, a pin could not be parsed)
      2  updates are available (lets a scheduled check branch on "is there
         anything to evaluate")

    Like the apt pin check, a registry query that fails is treated as a hard
    error (exit 1), never as "current": a silent false negative would leave the
    pin unmonitored while looking healthy.
#>

[CmdletBinding()]
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Location).Path

# --- The pin manifest --------------------------------------------------------
# Each tool: a display name, the registry to query, the registry package id, and
# one or more locations. A location is a file plus a .NET regex whose ENTIRE
# match is the version string (achieved with look-around), so applying a bump is
# a straight [regex]::Replace of the match with the new version. Multiple
# occurrences within a file are all replaced.
$tools = @(
    @{
        name      = '@playwright/mcp'
        registry  = 'npm'
        id        = '@playwright/mcp'
        locations = @(
            @{ file = '.devcontainer/setup.sh'; pattern = '(?<=PLAYWRIGHT_MCP_VERSION=")[^"]+' }
            @{ file = '.mcp.json';              pattern = '(?<=@playwright/mcp@)[^"]+' }
        )
    },
    @{
        name      = 'dotnet-ef'
        registry  = 'nuget'
        id        = 'dotnet-ef'
        locations = @(
            @{ file = '.devcontainer/setup.sh'; pattern = '(?<=dotnet-ef --version )\d[\w.\-]*' }
            @{ file = '.devcontainer/setup.sh'; pattern = '(?<=dotnet-ef )\d[\w.\-]*(?= installed globally)' }
        )
    }
)

# --- Registry queries --------------------------------------------------------

function Get-LatestNpmVersion {
    param([string]$Id)
    # Scoped names (@scope/name) must have the slash encoded for the registry path.
    $encoded = $Id -replace '/', '%2F'
    $resp = Invoke-RestMethod -Uri "https://registry.npmjs.org/$encoded" -TimeoutSec 30
    $latest = $resp.'dist-tags'.latest
    if (-not $latest) { throw "npm returned no dist-tags.latest for $Id." }
    return $latest
}

function Get-LatestNuGetVersion {
    param([string]$Id)
    # The flat-container id segment is lower-cased.
    $resp = Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/$($Id.ToLower())/index.json" -TimeoutSec 30
    # Versions are ascending; exclude prereleases (a '-' suffix) and take the last.
    $stable = @($resp.versions | Where-Object { $_ -notmatch '-' })
    if ($stable.Count -eq 0) { throw "NuGet returned no stable versions for $Id." }
    return $stable[-1]
}

function Compare-Version {
    # Returns $true if $Candidate is strictly newer than $Current.
    param([string]$Candidate, [string]$Current)
    try {
        return ([version]$Candidate) -gt ([version]$Current)
    } catch {
        throw "Could not compare versions '$Candidate' and '$Current' as [version]: $_"
    }
}

# --- Read the current pins ---------------------------------------------------

function Get-PinnedVersion {
    param([hashtable]$Location)
    $path = Join-Path $repoRoot $Location.file
    if (-not (Test-Path $path)) { throw "Pin file not found: $($Location.file)" }
    $text = Get-Content -Path $path -Raw
    $m = [regex]::Match($text, $Location.pattern)
    if (-not $m.Success) { throw "Pattern did not match in $($Location.file): $($Location.pattern)" }
    return $m.Value
}

# --- Detect ------------------------------------------------------------------

$queryFailures = @()
$findings = @()  # one per tool that is behind

foreach ($tool in $tools) {
    # Read every location's current version. They should all agree; if they have
    # drifted, use the lowest as "current" so we never propose a downgrade and
    # the -Apply rewrite heals all locations up to the latest.
    $currents = @()
    foreach ($loc in $tool.locations) {
        $currents += Get-PinnedVersion -Location $loc
    }
    $distinct = @($currents | Select-Object -Unique)
    if ($distinct.Count -gt 1) {
        Write-Host "WARNING: $($tool.name) is pinned inconsistently across files: $($distinct -join ', '). Will heal to the latest."
    }
    # Lowest current via [version] sort.
    $current = (@($distinct | Sort-Object { [version]$_ }))[0]

    try {
        $latest = switch ($tool.registry) {
            'npm'   { Get-LatestNpmVersion   -Id $tool.id }
            'nuget' { Get-LatestNuGetVersion -Id $tool.id }
            default { throw "Unknown registry '$($tool.registry)' for $($tool.name)." }
        }
    } catch {
        $queryFailures += $tool.name
        Write-Host "ERROR: could not query $($tool.registry) for $($tool.name): $_"
        continue
    }

    $behind = Compare-Version -Candidate $latest -Current $current
    $state = if ($behind) { 'UPDATE' } else { 'current' }
    Write-Host ("  {0,-22} {1,-12} -> {2,-12} {3}" -f $tool.name, $current, $latest, $state)

    if ($behind) {
        $findings += [pscustomobject]@{
            name    = $tool.name
            current = ($distinct -join ', ')
            latest  = $latest
            files   = (@($tool.locations | ForEach-Object { $_.file } | Select-Object -Unique) -join ', ')
            tool    = $tool
        }
    }
}

if ($queryFailures.Count -gt 0) {
    Write-Host ''
    Write-Host "FATAL: tooling pin check could not complete for: $($queryFailures -join ', ')"
    Write-Host 'Refusing to report a result; these pins are NOT confirmed current.'
    exit 1
}

Write-Host ''
if ($findings.Count -eq 0) {
    Write-Host 'All manually-pinned tooling is current. Nothing to evaluate.'
    exit 0
}

Write-Host "$($findings.Count) pinned tool(s) have a newer version available to evaluate."

# --- PR body + machine-readable outputs --------------------------------------

$bodyLines = @('| Tool | Pinned | Available | Files |', '| --- | --- | --- | --- |')
foreach ($f in $findings) {
    $bodyLines += "| ``$($f.name)`` | $($f.current) | $($f.latest) | $($f.files) |"
}
$body = $bodyLines -join "`n"
Set-Content -Path (Join-Path $repoRoot 'tooling-pin-pr-body.md') -Value $body -Encoding utf8

if ($env:GITHUB_OUTPUT) {
    "has_updates=true" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "update_count=$($findings.Count)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

# --- Apply -------------------------------------------------------------------

if ($Apply) {
    Write-Host ''
    Write-Host 'Applying bumps to pin files ...'
    foreach ($f in $findings) {
        foreach ($loc in $f.tool.locations) {
            $path = Join-Path $repoRoot $loc.file
            $text = Get-Content -Path $path -Raw
            $new  = [regex]::Replace($text, $loc.pattern, $f.latest)
            if ($new -ne $text) {
                Set-Content -Path $path -Value $new -NoNewline
                Write-Host "  $($loc.file): $($f.name) -> $($f.latest)"
            }
        }
    }
}

exit 2
