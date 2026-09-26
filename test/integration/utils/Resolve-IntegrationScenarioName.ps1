# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Resolves a -Scenario value to the canonical scenario name the runner works with.

.DESCRIPTION
    Dot-source this file. Run-IntegrationTests.ps1 calls Resolve-IntegrationScenarioName before
    it touches Docker, so an unknown scenario fails in seconds rather than after the whole stack
    has been stood up, and the short forms people type resolve to the real name:

      Scenario-005-MatchingRules, scenario-005-matchingrules, Invoke-Scenario-005-MatchingRules.ps1,
      5, 005, Scenario-005, MatchingRules  ->  Scenario-005-MatchingRules
      All                                  ->  All

    The canonical name matters beyond locating Invoke-<name>.ps1: the runner branches on it
    throughout (snapshot selection, directory-type restrictions, template relevance), so a short
    form that merely happened to find the script would still take the wrong branches. Numbers
    match exactly, never as a prefix, so 1 is Scenario 001 and not Scenario 010. The retired
    unpadded forms (Scenario5, Scenario5-MatchingRules) are deliberately not accepted.

    Get-IntegrationScenarioNumber turns a canonical name into its number, which is what the
    runner's scenario-specific branches compare (`$scenarioNumber -in 14, 19, 22`). Matching the
    name with wildcards instead once made "*Scenario1*" match Scenarios 010-019 too (#1762).
#>

function Resolve-IntegrationScenarioName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Scenario,

        # The directory holding the Invoke-Scenario*.ps1 scripts.
        [Parameter(Mandatory = $true)]
        [string]$ScenariosPath
    )

    $requested = $Scenario.Trim()
    if ($requested -eq 'All') { return 'All' }

    $names = @(Get-ChildItem -LiteralPath $ScenariosPath -Filter 'Invoke-*.ps1' |
        Sort-Object { if ($_.BaseName -match 'Scenario-(\d{3})-') { [int]$Matches[1] } else { [int]::MaxValue } }, Name |
        ForEach-Object { $_.BaseName.Substring('Invoke-'.Length) })

    $candidate = $requested
    if ($candidate -like 'Invoke-*') { $candidate = $candidate.Substring('Invoke-'.Length) }
    if ($candidate -like '*.ps1') { $candidate = $candidate.Substring(0, $candidate.Length - '.ps1'.Length) }

    # PowerShell's -eq and -match compare case-insensitively, which is what is wanted here.
    $match = @($names | Where-Object { $_ -eq $candidate })
    if ($match.Count -eq 0 -and $candidate -match '^(?:Scenario-)?(\d{1,3})$') {
        $number = [int]$Matches[1]
        $match = @($names | Where-Object { $_ -match '^Scenario-(\d{3})-' -and [int]$Matches[1] -eq $number })
    }
    if ($match.Count -eq 0) {
        $match = @($names | Where-Object { $_ -match '^Scenario-\d{3}-(.+)$' -and $Matches[1] -eq $candidate })
    }

    if ($match.Count -eq 1) { return $match[0] }

    $reason = if ($match.Count -gt 1) { "matches more than one scenario ($($match -join ', '))" } else { 'matches no scenario' }
    throw "Unknown scenario '$requested': it $reason. Pass a number (5 or 005), Scenario-NNN (Scenario-005) or one of:`n  All`n  $($names -join "`n  ")"
}

function Get-IntegrationScenarioNumber {
    <#
    .SYNOPSIS
        The number of a canonical scenario name (Scenario-014-AttributePriority -> 14), or $null for
        All or any name that is not a numbered scenario.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Scenario
    )

    if ($Scenario -match '^Scenario-(\d{3})-') { return [int]$Matches[1] }
    return $null
}
