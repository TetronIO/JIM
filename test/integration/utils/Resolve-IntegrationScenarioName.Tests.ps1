# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Resolve-IntegrationScenarioName.

.DESCRIPTION
    Run-IntegrationTests.ps1 resolves -Scenario before it touches Docker, so a short form
    ("5", "Scenario5", "MatchingRules") runs the right scenario and an unknown name fails in
    seconds with the valid names listed, rather than after the whole stack has been stood up.
    A throwaway scenarios directory keeps these tests independent of the real scenario set.
#>

BeforeAll {
    . "$PSScriptRoot/Resolve-IntegrationScenarioName.ps1"

    $script:scenariosPath = Join-Path ([System.IO.Path]::GetTempPath()) "jim-scenario-names-$([System.Guid]::NewGuid())"
    New-Item -ItemType Directory -Path $script:scenariosPath -Force | Out-Null
    foreach ($name in @('Scenario1-HRToIdentityDirectory', 'Scenario5-MatchingRules', 'Scenario10-SyncRuleScoping', 'Scenario14-AttributePriority')) {
        Set-Content -LiteralPath (Join-Path $script:scenariosPath "Invoke-$name.ps1") -Value '# scenario'
    }
}

AfterAll {
    Remove-Item -LiteralPath $script:scenariosPath -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Resolve-IntegrationScenarioName' {
    It 'returns a full scenario name unchanged' {
        Resolve-IntegrationScenarioName -Scenario 'Scenario5-MatchingRules' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario5-MatchingRules'
    }

    It 'corrects the case of a full scenario name' {
        Resolve-IntegrationScenarioName -Scenario 'scenario5-matchingrules' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario5-MatchingRules'
    }

    It 'accepts the script file name' {
        Resolve-IntegrationScenarioName -Scenario 'Invoke-Scenario5-MatchingRules.ps1' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario5-MatchingRules'
    }

    It 'resolves a bare number' {
        Resolve-IntegrationScenarioName -Scenario '5' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario5-MatchingRules'
    }

    It 'resolves ScenarioN' {
        Resolve-IntegrationScenarioName -Scenario 'Scenario5' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario5-MatchingRules'
    }

    It 'resolves the number exactly, never as a prefix (1 is not 10 or 14)' {
        Resolve-IntegrationScenarioName -Scenario '1' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario1-HRToIdentityDirectory'
        Resolve-IntegrationScenarioName -Scenario '10' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario10-SyncRuleScoping'
    }

    It 'resolves the descriptive part on its own' {
        Resolve-IntegrationScenarioName -Scenario 'attributepriority' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario14-AttributePriority'
    }

    It 'passes All through' {
        Resolve-IntegrationScenarioName -Scenario 'all' -ScenariosPath $script:scenariosPath | Should -Be 'All'
    }

    It 'trims surrounding whitespace' {
        Resolve-IntegrationScenarioName -Scenario ' 5 ' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario5-MatchingRules'
    }

    It 'throws for a number with no scenario, listing the valid names' {
        { Resolve-IntegrationScenarioName -Scenario '99' -ScenariosPath $script:scenariosPath } |
            Should -Throw -ExpectedMessage "*Unknown scenario '99'*Scenario5-MatchingRules*"
    }

    It 'throws for an unknown name' {
        { Resolve-IntegrationScenarioName -Scenario 'MatchingRule' -ScenariosPath $script:scenariosPath } |
            Should -Throw -ExpectedMessage "*Unknown scenario 'MatchingRule'*"
    }

    It 'lists the valid names in numeric order' {
        try { Resolve-IntegrationScenarioName -Scenario 'nope' -ScenariosPath $script:scenariosPath } catch { $message = $_.Exception.Message }
        $message.IndexOf('Scenario5-MatchingRules') | Should -BeLessThan $message.IndexOf('Scenario10-SyncRuleScoping')
    }

    It 'resolves every real scenario by its number' {
        $realPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scenarios'
        foreach ($file in Get-ChildItem $realPath -Filter 'Invoke-Scenario*.ps1') {
            $name = $file.BaseName.Substring('Invoke-'.Length)
            $number = [regex]::Match($name, '^Scenario(\d+)-').Groups[1].Value
            Resolve-IntegrationScenarioName -Scenario $number -ScenariosPath $realPath | Should -Be $name
        }
    }
}

Describe 'Get-IntegrationScenarioNumber' {
    It 'returns the number of a canonical scenario name as an integer' {
        $number = Get-IntegrationScenarioNumber -Scenario 'Scenario14-AttributePriority'
        $number | Should -Be 14
        $number | Should -BeOfType [int]
    }

    It 'distinguishes Scenario 1 from Scenarios 10-19' {
        Get-IntegrationScenarioNumber -Scenario 'Scenario1-HRToIdentityDirectory' | Should -Be 1
        Get-IntegrationScenarioNumber -Scenario 'Scenario19-AuxiliaryClasses' | Should -Be 19
    }

    It 'returns null for All, so no scenario-specific branch matches it' {
        Get-IntegrationScenarioNumber -Scenario 'All' | Should -BeNullOrEmpty
    }

    It 'returns null when no scenario has been chosen yet (the interactive menu comes later)' {
        Get-IntegrationScenarioNumber -Scenario $null | Should -BeNullOrEmpty
    }

    It 'returns null for a name that is not a scenario' {
        Get-IntegrationScenarioNumber -Scenario 'Pre-Release' | Should -BeNullOrEmpty
    }
}
