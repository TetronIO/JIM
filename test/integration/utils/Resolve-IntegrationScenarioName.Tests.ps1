# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Resolve-IntegrationScenarioName.

.DESCRIPTION
    Run-IntegrationTests.ps1 resolves -Scenario before it touches Docker, so a short form
    ("5", "005", "Scenario-005", "MatchingRules") runs the right scenario and an unknown name fails in
    seconds with the valid names listed, rather than after the whole stack has been stood up.
    A throwaway scenarios directory keeps these tests independent of the real scenario set.
#>

BeforeAll {
    . "$PSScriptRoot/Resolve-IntegrationScenarioName.ps1"

    $script:scenariosPath = Join-Path ([System.IO.Path]::GetTempPath()) "jim-scenario-names-$([System.Guid]::NewGuid())"
    New-Item -ItemType Directory -Path $script:scenariosPath -Force | Out-Null
    foreach ($name in @('Scenario-001-HRToIdentityDirectory', 'Scenario-005-MatchingRules', 'Scenario-010-SyncRuleScoping', 'Scenario-014-AttributePriority')) {
        Set-Content -LiteralPath (Join-Path $script:scenariosPath "Invoke-$name.ps1") -Value '# scenario'
    }
}

AfterAll {
    Remove-Item -LiteralPath $script:scenariosPath -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Resolve-IntegrationScenarioName' {
    It 'returns a full scenario name unchanged' {
        Resolve-IntegrationScenarioName -Scenario 'Scenario-005-MatchingRules' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-005-MatchingRules'
    }

    It 'corrects the case of a full scenario name' {
        Resolve-IntegrationScenarioName -Scenario 'scenario-005-matchingrules' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-005-MatchingRules'
    }

    It 'accepts the script file name' {
        Resolve-IntegrationScenarioName -Scenario 'Invoke-Scenario-005-MatchingRules.ps1' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-005-MatchingRules'
    }

    It 'resolves a bare number' {
        Resolve-IntegrationScenarioName -Scenario '5' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-005-MatchingRules'
    }

    It 'resolves a zero-padded number' {
        Resolve-IntegrationScenarioName -Scenario '005' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-005-MatchingRules'
    }

    It 'resolves Scenario-NNN' {
        Resolve-IntegrationScenarioName -Scenario 'Scenario-005' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-005-MatchingRules'
    }

    It 'rejects the retired unpadded forms' {
        { Resolve-IntegrationScenarioName -Scenario 'Scenario5' -ScenariosPath $script:scenariosPath } |
            Should -Throw -ExpectedMessage "*Unknown scenario 'Scenario5'*"
        { Resolve-IntegrationScenarioName -Scenario 'Scenario5-MatchingRules' -ScenariosPath $script:scenariosPath } |
            Should -Throw -ExpectedMessage "*Unknown scenario 'Scenario5-MatchingRules'*"
    }

    It 'resolves the number exactly, never as a prefix (1 is not 10 or 14)' {
        Resolve-IntegrationScenarioName -Scenario '1' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-001-HRToIdentityDirectory'
        Resolve-IntegrationScenarioName -Scenario '10' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-010-SyncRuleScoping'
        Resolve-IntegrationScenarioName -Scenario '001' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-001-HRToIdentityDirectory'
    }

    It 'resolves the descriptive part on its own' {
        Resolve-IntegrationScenarioName -Scenario 'attributepriority' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-014-AttributePriority'
    }

    It 'passes All through' {
        Resolve-IntegrationScenarioName -Scenario 'all' -ScenariosPath $script:scenariosPath | Should -Be 'All'
    }

    It 'trims surrounding whitespace' {
        Resolve-IntegrationScenarioName -Scenario ' 5 ' -ScenariosPath $script:scenariosPath | Should -Be 'Scenario-005-MatchingRules'
    }

    It 'throws for a number with no scenario, listing the valid names' {
        { Resolve-IntegrationScenarioName -Scenario '99' -ScenariosPath $script:scenariosPath } |
            Should -Throw -ExpectedMessage "*Unknown scenario '99'*Scenario-005-MatchingRules*"
    }

    It 'throws for an unknown name' {
        { Resolve-IntegrationScenarioName -Scenario 'MatchingRule' -ScenariosPath $script:scenariosPath } |
            Should -Throw -ExpectedMessage "*Unknown scenario 'MatchingRule'*"
    }

    It 'lists the valid names in numeric order' {
        try { Resolve-IntegrationScenarioName -Scenario 'nope' -ScenariosPath $script:scenariosPath } catch { $message = $_.Exception.Message }
        $message.IndexOf('Scenario-005-MatchingRules') | Should -BeLessThan $message.IndexOf('Scenario-010-SyncRuleScoping')
    }

    It 'resolves every real scenario by its number' {
        $realPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scenarios'
        foreach ($file in Get-ChildItem $realPath -Filter 'Invoke-Scenario-*.ps1') {
            $name = $file.BaseName.Substring('Invoke-'.Length)
            $number = [regex]::Match($name, '^Scenario-(\d{3})-').Groups[1].Value
            Resolve-IntegrationScenarioName -Scenario $number -ScenariosPath $realPath | Should -Be $name
        }
    }
}

Describe 'Get-IntegrationScenarioNumber' {
    It 'returns the number of a canonical scenario name as an integer' {
        $number = Get-IntegrationScenarioNumber -Scenario 'Scenario-014-AttributePriority'
        $number | Should -Be 14
        $number | Should -BeOfType [int]
    }

    It 'distinguishes Scenario 001 from Scenarios 010-019' {
        Get-IntegrationScenarioNumber -Scenario 'Scenario-001-HRToIdentityDirectory' | Should -Be 1
        Get-IntegrationScenarioNumber -Scenario 'Scenario-019-AuxiliaryClasses' | Should -Be 19
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
