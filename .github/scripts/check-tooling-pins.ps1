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
      4. Checks that the pinned version ITSELF is still published. A version that
         has been unpublished (npm) or deleted (NuGet) cannot be installed, so
         the devcontainer can no longer be built, and asking only "is something
         newer available?" would report that pin as perfectly current. This is
         the tooling equivalent of #1374, where an Ubuntu archive rotation left
         `main` unable to build the Worker image with every check green. Both
         registry documents already carry the full version list, so it costs no
         extra request.

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
    Exit codes (the highest applicable one wins):
      0  success; no updates available (or -Apply applied them cleanly)
      1  an error occurred (a registry query failed, a pin could not be parsed)
      2  updates are available (lets a scheduled check branch on "is there
         anything to evaluate")
      3  a pinned version is no longer published upstream, so it can no longer be
         installed. Reported after any bumps have been applied and written out,
         so the workflow can still raise the PR that fixes it and then fail the
         run.

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

# Both return the latest stable version AND every version the registry still
# offers, so the caller can ask the two separate questions: is the pin behind,
# and is the pin still there at all.

function Get-NpmRegistryInfo {
    param([string]$Id)
    # Scoped names (@scope/name) must have the slash encoded for the registry path.
    $encoded = $Id -replace '/', '%2F'
    $resp = Invoke-RestMethod -Uri "https://registry.npmjs.org/$encoded" -TimeoutSec 30
    $latest = $resp.'dist-tags'.latest
    if (-not $latest) { throw "npm returned no dist-tags.latest for $Id." }
    # An unpublished version is removed from the packument's versions map, which
    # is exactly what "this pin can no longer be installed" looks like on npm.
    $versions = @($resp.versions.PSObject.Properties.Name)
    if ($versions.Count -eq 0) { throw "npm returned no versions for $Id." }
    return [pscustomobject]@{ latest = $latest; versions = $versions }
}

function Get-NuGetRegistryInfo {
    param([string]$Id)
    # The flat-container id segment is lower-cased.
    $resp = Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/$($Id.ToLower())/index.json" -TimeoutSec 30
    $versions = @($resp.versions)
    if ($versions.Count -eq 0) { throw "NuGet returned no versions for $Id." }
    # Versions are ascending; exclude prereleases (a '-' suffix) and take the last.
    $stable = @($versions | Where-Object { $_ -notmatch '-' })
    if ($stable.Count -eq 0) { throw "NuGet returned no stable versions for $Id." }
    return [pscustomobject]@{ latest = $stable[-1]; versions = $versions }
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
$findings = @()      # one per tool that is behind
$withdrawn = @()     # one per tool whose pinned version is no longer published

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
        $info = switch ($tool.registry) {
            'npm'   { Get-NpmRegistryInfo   -Id $tool.id }
            'nuget' { Get-NuGetRegistryInfo -Id $tool.id }
            default { throw "Unknown registry '$($tool.registry)' for $($tool.name)." }
        }
    } catch {
        $queryFailures += $tool.name
        Write-Host "ERROR: could not query $($tool.registry) for $($tool.name): $_"
        continue
    }

    $latest = $info.latest

    # Every version actually pinned in a file has to still exist upstream, not
    # just the lowest: a drifted pair could have one live location and one dead.
    $gone = @($distinct | Where-Object { $info.versions -notcontains $_ })

    $behind = Compare-Version -Candidate $latest -Current $current
    $state = if ($gone.Count -gt 0) { "WITHDRAWN from $($tool.registry)" }
             elseif ($behind) { 'UPDATE' }
             else { 'current' }
    Write-Host ("  {0,-22} {1,-12} -> {2,-12} {3}" -f $tool.name, $current, $latest, $state)

    if ($gone.Count -gt 0) {
        $withdrawn += [pscustomobject]@{
            name     = $tool.name
            registry = $tool.registry
            versions = ($gone -join ', ')
            files    = (@($tool.locations | ForEach-Object { $_.file } | Select-Object -Unique) -join ', ')
        }
    }

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

# --- PR body + apply ---------------------------------------------------------

# Kept in a function so it can run before the withdrawn-pin check decides the
# exit code: a pin the registry has dropped needs BOTH the loud failure and the
# bump PR that fixes it, and a bare `exit 3` above this would suppress the very
# proposal that clears the failure.
function Publish-BumpProposal {
    $bodyLines = @('| Tool | Pinned | Available | Files |', '| --- | --- | --- | --- |')
    foreach ($f in $findings) {
        $bodyLines += "| ``$($f.name)`` | $($f.current) | $($f.latest) | $($f.files) |"
    }
    Set-Content -Path (Join-Path $repoRoot 'tooling-pin-pr-body.md') -Value ($bodyLines -join "`n") -Encoding utf8

    if (-not $Apply) { return }

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

Write-Host ''

if ($env:GITHUB_OUTPUT) {
    "has_updates=$($findings.Count -gt 0)".ToLowerInvariant() | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "update_count=$($findings.Count)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "unavailable=$($withdrawn.Count -gt 0)".ToLowerInvariant() | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "unavailable_tools=$(@($withdrawn | ForEach-Object { $_.name }) -join ', ')" |
        Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

if ($findings.Count -eq 0) {
    Write-Host 'All manually-pinned tooling is current. Nothing to evaluate.'
    # Not `exit 0`: a pin the registry no longer offers has to be reported
    # whether or not there is a bump to propose, and the exit-code decision is
    # made once, at the end of the script.
} else {
    Write-Host "$($findings.Count) pinned tool(s) have a newer version available to evaluate."
    Publish-BumpProposal
}

if ($withdrawn.Count -gt 0) {
    Write-Host ''
    Write-Host "FATAL: $($withdrawn.Count) pinned tool version(s) are no longer published upstream:"
    foreach ($w in $withdrawn) {
        Write-Host "  $($w.name) $($w.versions) is gone from $($w.registry) (pinned in $($w.files))"
    }
    Write-Host 'That version can no longer be installed, so the devcontainer cannot be built from these pins.'
    Write-Host 'Land the bump above, or repin by hand to a version the registry still offers.'
    exit 3
}

if ($findings.Count -eq 0) { exit 0 }
exit 2
