# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Removes CodeQL SARIF results whose primary location falls under a path excluded by the
    CodeQL configuration's paths-ignore block.

.DESCRIPTION
    The codeql-action applies a configuration's paths-ignore to what gets EXTRACTED, which works for
    interpreted languages but not for compiled ones: C# is always built and analysed whole, and the
    action does not filter the resulting alerts by path. Alerts in excluded paths therefore re-mint on
    every analysis, and dismissing them in the UI does not stop the next analysis recreating them
    (observed on PR #1177, where the test-only SCIM service provider's JSON echo re-minted a
    cross-site scripting alert on every push despite the path being excluded).

    This script is the enforcement point: the CodeQL workflow runs the analysis with upload disabled,
    filters each produced SARIF file through this script, and uploads what remains. The exclusion
    list is read from the same paths-ignore block the interpreted languages use, so there is exactly
    one place a path is excluded, and this script cannot drift from it.

    Two rules decide what is removed:

      1. A result whose primary location is inside an excluded path.

      2. A result whose taint source is inside an excluded path. Unit tests read fixture hostnames
         from environment variables and hand them to the connector under test; CodeQL treats the
         environment variable read as sensitive, follows it into the connector's own log calls, and
         reports "clear-text storage" against shipped code. The alert lives in src/, so rule 1 never
         sees it, but the only thing that makes it an alert is the excluded test file at the other
         end of the flow. A result is removed on this rule only when EVERY code flow it carries
         starts in an excluded path: one flow from shipped code is a real finding.

    Every removed result is logged individually (rule id, file, and for rule 2 the excluded source),
    so a filtered upload never reads as "the analysis found nothing there"; the log says what was
    dropped and why.

.PARAMETER SarifDirectory
    Directory containing the .sarif file(s) the analysis produced. Searched recursively; the files
    are rewritten in place.

.PARAMETER ConfigFile
    The CodeQL configuration file whose paths-ignore block defines the exclusions.

.EXAMPLE
    ./scripts/Filter-Sarif.ps1 -SarifDirectory sarif-results -ConfigFile .github/codeql/codeql-config.yml
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SarifDirectory,

    [Parameter(Mandatory)]
    [string]$ConfigFile
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ConfigFile)) {
    throw "CodeQL configuration file not found: $ConfigFile"
}
if (-not (Test-Path $SarifDirectory)) {
    throw "SARIF directory not found: $SarifDirectory"
}

# Read the paths-ignore block from the configuration. A dedicated YAML parser is deliberately not
# used: the block is a flat list of strings, and this script must not add a module dependency to the
# analysis job. The block ends at the first line that is neither a list item, a comment, nor blank.
$ignoreGlobs = [System.Collections.Generic.List[string]]::new()
$inBlock = $false
foreach ($line in Get-Content $ConfigFile) {
    if ($line -match '^\s*paths-ignore\s*:\s*$') {
        $inBlock = $true
        continue
    }
    if ($inBlock) {
        if ($line -match '^\s*-\s*(.+?)\s*$') {
            $ignoreGlobs.Add($Matches[1].Trim('"').Trim("'"))
        }
        elseif ($line -match '^\s*(#.*)?$') {
            continue
        }
        else {
            $inBlock = $false
        }
    }
}

if ($ignoreGlobs.Count -eq 0) {
    throw "No paths-ignore entries found in ${ConfigFile}: nothing to enforce. If the block was deliberately emptied, remove the filter step from the workflow too."
}

# Convert each glob to an anchored regex: '**' matches across path separators, '*' within a segment.
# The double-star is parked behind a token first so the single-star replacement cannot consume it.
$ignorePatterns = foreach ($glob in $ignoreGlobs) {
    $escaped = [regex]::Escape($glob)
    $escaped = $escaped.Replace('\*\*', '@@DOUBLESTAR@@')
    $escaped = $escaped.Replace('\*', '[^/]*')
    $escaped = $escaped.Replace('@@DOUBLESTAR@@', '.*')
    [regex]::new("^$escaped$")
}

Write-Host "Filtering SARIF results against $($ignoreGlobs.Count) excluded path pattern(s) from ${ConfigFile}:"
$ignoreGlobs | ForEach-Object { Write-Host "  - $_" }

function Test-ExcludedPath([string]$uri) {
    foreach ($pattern in $script:ignorePatterns) {
        if ($pattern.IsMatch($uri)) { return $true }
    }
    return $false
}

# Rule 2 above. A result carrying code flows is a taint-tracking finding, and each flow's first
# step is the source that makes it one. Returns the distinct excluded source paths when every flow
# starts inside an excluded path, and $null otherwise (no flows, or at least one flow from code
# that is in scope). The comma keeps a single-element result an array rather than a bare string.
function Get-ExcludedSources($result) {
    $flows = @($result.codeFlows | Where-Object { $null -ne $_ })
    if ($flows.Count -eq 0) { return $null }

    $sources = [System.Collections.Generic.List[string]]::new()
    foreach ($flow in $flows) {
        $first = $flow.threadFlows[0].locations[0].location.physicalLocation.artifactLocation.uri
        if (-not $first -or -not (Test-ExcludedPath $first)) { return $null }
        if (-not $sources.Contains($first)) { $sources.Add($first) }
    }
    return ,$sources.ToArray()
}

$sarifFiles = @(Get-ChildItem -Path $SarifDirectory -Filter '*.sarif' -Recurse -File)
if ($sarifFiles.Count -eq 0) {
    throw "No .sarif files found under ${SarifDirectory}. The analysis output path and the filter step's input have diverged."
}

foreach ($file in $sarifFiles) {
    $sarif = Get-Content $file.FullName -Raw | ConvertFrom-Json -Depth 100
    $totalKept = 0
    $totalRemoved = 0

    foreach ($run in $sarif.runs) {
        if ($null -eq $run.results) { continue }

        $kept = [System.Collections.Generic.List[object]]::new()
        foreach ($result in $run.results) {
            # A result is reported at its primary location; that is the path the exclusion applies
            # to. Results without one (possible per the SARIF specification) are kept: this filter
            # only removes what it can positively place inside an excluded path.
            $uri = if ($result.locations) { $result.locations[0].physicalLocation.artifactLocation.uri } else { $null }
            if ($uri -and (Test-ExcludedPath $uri)) {
                $totalRemoved++
                Write-Host "  Removed: $($result.ruleId) at $uri"
                continue
            }

            $excludedSources = Get-ExcludedSources $result
            if ($excludedSources) {
                $totalRemoved++
                Write-Host "  Removed: $($result.ruleId) at $uri (every code flow starts in an excluded path: $($excludedSources -join ', '))"
                continue
            }

            $kept.Add($result)
        }

        $run.results = $kept.ToArray()
        $totalKept += $kept.Count
    }

    if ($totalRemoved -gt 0) {
        $sarif | ConvertTo-Json -Depth 100 -Compress | Set-Content $file.FullName -NoNewline
    }
    Write-Host "$($file.Name): kept $totalKept result(s), removed $totalRemoved reported in, or sourced from, excluded paths."
}
