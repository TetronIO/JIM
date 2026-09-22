# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Resolves a -Scenario value to the canonical scenario name the runner works with.

.DESCRIPTION
    Dot-source this file. Run-IntegrationTests.ps1 calls Resolve-IntegrationScenarioName before
    it touches Docker, so an unknown scenario fails in seconds rather than after the whole stack
    has been stood up, and the short forms people type resolve to the real name:

      Scenario5-MatchingRules, scenario5-matchingrules, Invoke-Scenario5-MatchingRules.ps1,
      5, Scenario5, MatchingRules  ->  Scenario5-MatchingRules
      All                          ->  All

    The canonical name matters beyond locating Invoke-<name>.ps1: the runner branches on it
    throughout (snapshot selection, directory-type restrictions, template relevance), so a short
    form that merely happened to find the script would still take the wrong branches. Numbers
    match exactly, never as a prefix, so 1 is Scenario 1 and not Scenario 10.
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
        Sort-Object { if ($_.BaseName -match 'Scenario(\d+)') { [int]$Matches[1] } else { [int]::MaxValue } }, Name |
        ForEach-Object { $_.BaseName.Substring('Invoke-'.Length) })

    $candidate = $requested
    if ($candidate -like 'Invoke-*') { $candidate = $candidate.Substring('Invoke-'.Length) }
    if ($candidate -like '*.ps1') { $candidate = $candidate.Substring(0, $candidate.Length - '.ps1'.Length) }

    # PowerShell's -eq and -match compare case-insensitively, which is what is wanted here.
    $match = @($names | Where-Object { $_ -eq $candidate })
    if ($match.Count -eq 0 -and $candidate -match '^(?:Scenario)?(\d+)$') {
        $number = [int]$Matches[1]
        $match = @($names | Where-Object { $_ -match '^Scenario(\d+)-' -and [int]$Matches[1] -eq $number })
    }
    if ($match.Count -eq 0) {
        $match = @($names | Where-Object { $_ -match '^Scenario\d+-(.+)$' -and $Matches[1] -eq $candidate })
    }

    if ($match.Count -eq 1) { return $match[0] }

    $reason = if ($match.Count -gt 1) { "matches more than one scenario ($($match -join ', '))" } else { 'matches no scenario' }
    throw "Unknown scenario '$requested': it $reason. Pass a number (5), ScenarioN (Scenario5) or one of:`n  All`n  $($names -join "`n  ")"
}
