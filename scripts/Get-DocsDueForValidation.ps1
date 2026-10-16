# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Lists the priority docs pages due for re-validation before a release.

.DESCRIPTION
    Each priority page (docs/.conformance/config.psd1, PriorityPages) records the
    release it was last checked against in its front matter, as validated: vX.Y.Z
    (PRI-1 in engineering/DOCUMENTATION_STANDARD.md). /release runs this script with
    the version being released, and every page it lists is either re-validated (read
    against the product, corrected, and its validated: set to the new version) or
    deferred with a reason in the release pull request.

    A page is due when it records no validation, an earlier one, or one that cannot
    be read. The script prints each page due and why, then a summary line; with
    -PassThru it returns them instead, as objects with Page, Validated and Reason. A
    priority page that does not exist is an error: leaving it off the list would let it
    ship unchecked.

.PARAMETER Version
    The version being released, with or without its leading v (0.17.0 or v0.17.0).

.PARAMETER DocsPath
    The docs folder. Defaults to docs.

.PARAMETER ConfigPath
    The conformance configuration that lists the priority pages. Defaults to
    docs/.conformance/config.psd1.

.PARAMETER PassThru
    Return the pages due as objects rather than printing them.

.EXAMPLE
    pwsh ./scripts/Get-DocsDueForValidation.ps1 -Version 0.17.0

    Lists every priority page not yet validated against v0.17.0, and why.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$DocsPath = 'docs',
    [string]$ConfigPath = 'docs/.conformance/config.psd1',
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'

$release = $null
if (-not [System.Management.Automation.SemanticVersion]::TryParse($Version.TrimStart('v'), [ref]$release)) {
    throw "'$Version' is not a version; give the version being released, such as 0.17.0."
}

$config = Import-PowerShellDataFile -LiteralPath $ConfigPath
$pages = @($config.PriorityPages)

$due = foreach ($relative in $pages) {
    $full = Join-Path $DocsPath $relative
    if (-not (Test-Path -LiteralPath $full)) {
        throw "Priority page '$relative' is listed in $ConfigPath but does not exist under $DocsPath."
    }

    # The front matter is the block between the first two --- lines.
    $validated = $null
    $lines = @(Get-Content -LiteralPath $full -TotalCount 40)
    if ($lines.Count -gt 0 -and $lines[0].Trim() -eq '---') {
        for ($i = 1; $i -lt $lines.Count -and $lines[$i].Trim() -ne '---'; $i++) {
            if ($lines[$i] -match '^validated:\s*(?<value>.*?)\s*$') {
                $validated = $Matches.value.Trim('"', "'")
            }
        }
    }

    $recorded = $null
    $reason = if (-not $validated) {
        'never validated'
    }
    elseif ($validated -notmatch '^v(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$' -or
        -not [System.Management.Automation.SemanticVersion]::TryParse($Matches.version, [ref]$recorded)) {
        "validated: '$validated' cannot be read as a release"
    }
    elseif ($recorded -lt $release) {
        "last validated against $validated"
    }

    if ($reason) {
        [pscustomobject]@{ Page = $relative; Validated = $validated; Reason = $reason }
    }
}

$due = @($due)
if ($PassThru) { return $due }

foreach ($page in $due) { Write-Host ("  {0,-52} {1}" -f $page.Page, $page.Reason) }
Write-Host ("{0} of {1} priority pages due for re-validation against v{2}." -f $due.Count, $pages.Count, $release)
