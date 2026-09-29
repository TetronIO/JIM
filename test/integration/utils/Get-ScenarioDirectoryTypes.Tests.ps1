# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-ScenarioDirectoryTypes.

.DESCRIPTION
    Run-IntegrationTests.ps1 asks these two questions in several places: which directory types can
    a scenario run against (the interactive menu, an explicit -DirectoryType, the -DirectoryType All
    expansion, the -Scenario All sweep filter), and does a scenario use a directory at all (so a
    -DirectoryType All run doesn't repeat a directory-agnostic scenario once per directory type).
    These tests pin down the answers for the scenarios with special rules, plus a scenario with no
    restriction, so a future hand-copy of the wrong list is caught here rather than at the next
    Pre-Release run. The table is the ONLY place the runner learns which directory types a scenario
    supports, including the Active Directory lab (Scenarios 024 and 025 run there only), so the last
    Describe here also pins the runner to it: no second list, no substitution.
#>

BeforeAll {
    . "$PSScriptRoot/Get-ScenarioDirectoryTypes.ps1"
}

Describe 'Get-ScenarioSupportedDirectoryTypes' {
    It 'restricts Scenario 014 (Attribute Priority) to OpenLDAP' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 14 | Should -Be @('OpenLDAP')
    }

    It 'restricts Scenario 019 (Auxiliary Classes) to OpenLDAP' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 19 | Should -Be @('OpenLDAP')
    }

    It 'restricts Scenario 022 (OpenLDAP Password Policy) to OpenLDAP' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 22 | Should -Be @('OpenLDAP')
    }

    It 'restricts Scenario 017 (Initial Password) to Samba AD and the Active Directory lab' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 17 | Should -Be @('SambaAD', 'ActiveDirectory')
    }

    It 'restricts Scenario 023 (Unique Value Generation) to Samba AD, OpenLDAP and the Active Directory lab, excluding 389 Directory Server' {
        $result = Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 23
        $result | Should -Be @('SambaAD', 'OpenLDAP', 'ActiveDirectory')
        $result | Should -Not -Contain 'DirectoryServer389'
    }

    It 'restricts Scenarios 024 and 025 (Active Directory lab only) to ActiveDirectory' {
        foreach ($number in 24, 25) {
            Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $number | Should -Be @('ActiveDirectory')
        }
    }

    It 'returns all four directory types for a scenario with no restriction' {
        $result = Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 1
        $result | Should -Be @('SambaAD', 'OpenLDAP', 'DirectoryServer389', 'ActiveDirectory')
    }

    It 'returns all four directory types for the directory-agnostic scenarios too (011, 015, 016)' {
        # Directory-agnostic is a separate question (Test-ScenarioIsDirectoryAgnostic): these
        # scenarios are architecturally able to run against any directory, they just don't need one.
        foreach ($number in 11, 15, 16) {
            Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $number | Should -Be @('SambaAD', 'OpenLDAP', 'DirectoryServer389', 'ActiveDirectory')
        }
    }

    It 'always returns an array, even for a single-directory-type restriction' {
        $result = @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 24)
        $result.GetType().IsArray | Should -Be $true
        $result.Count | Should -Be 1
    }

    It 'returns all four directory types for $null (the "All" scenario has no restriction)' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $null | Should -Be @('SambaAD', 'OpenLDAP', 'DirectoryServer389', 'ActiveDirectory')
    }

    It 'runs the Active Directory lab on whatever Samba AD runs, except the scenarios only a real domain controller can hold' {
        # The lab stands in for Samba AD everywhere but 024 and 025, so a scenario supports one if and
        # only if it supports the other. That is what let the -Scenario All sweep drop its
        # "judge an ActiveDirectory sweep by Samba AD" substitution.
        foreach ($number in 1..40) {
            $supported = @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $number)
            if ($number -in 24, 25) {
                $supported | Should -Be @('ActiveDirectory') -Because "Scenario $number needs a real domain controller"
            }
            elseif ($number -in 14, 19, 22) {
                $supported | Should -Not -Contain 'ActiveDirectory' -Because "Scenario $number is OpenLDAP only"
            }
            else {
                ('ActiveDirectory' -in $supported) | Should -Be ('SambaAD' -in $supported) -Because "Scenario $number runs on the lab exactly where it runs on Samba AD"
            }
        }
    }

    It 'lists every scenario''s directory types in the canonical order (SambaAD, OpenLDAP, DirectoryServer389, ActiveDirectory)' {
        $canonical = @('SambaAD', 'OpenLDAP', 'DirectoryServer389', 'ActiveDirectory')
        foreach ($number in (1..40) + $null) {
            $supported = @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $number)
            $supported | Should -Be @($canonical | Where-Object { $_ -in $supported }) -Because "the order for Scenario $number is what the -DirectoryType All legs run in"
        }
    }
}

Describe 'Get-ContainerDirectoryType' {
    It 'returns the three container directory types in the canonical order' {
        Get-ContainerDirectoryType | Should -Be @('SambaAD', 'OpenLDAP', 'DirectoryServer389')
    }

    It 'never includes ActiveDirectory, which needs the lab host and so is asked for by name' {
        Get-ContainerDirectoryType | Should -Not -Contain 'ActiveDirectory'
    }

    It 'always returns an array' {
        $result = @(Get-ContainerDirectoryType)
        $result.GetType().IsArray | Should -Be $true
        $result.Count | Should -Be 3
    }

    It 'leaves what a -DirectoryType All run expands to for a scenario once intersected with the table' {
        # This is the expansion the runner performs: the container types the scenario supports, in order.
        $expected = @{
            1  = @('SambaAD', 'OpenLDAP', 'DirectoryServer389')
            14 = @('OpenLDAP')
            17 = @('SambaAD')
            19 = @('OpenLDAP')
            22 = @('OpenLDAP')
            23 = @('SambaAD', 'OpenLDAP')
        }
        foreach ($number in $expected.Keys) {
            $supported = @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $number)
            $legs = @(Get-ContainerDirectoryType | Where-Object { $_ -in $supported })
            $legs | Should -Be $expected[$number] -Because "-DirectoryType All on Scenario $number"
        }
    }

    It 'leaves nothing for Scenarios 024 and 025 once intersected with the table, which the runner refuses' {
        foreach ($number in 24, 25) {
            $supported = @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $number)
            @(Get-ContainerDirectoryType | Where-Object { $_ -in $supported }).Count | Should -Be 0 -Because "-DirectoryType All never includes ActiveDirectory"
        }
    }
}

Describe 'Test-ScenarioIsDirectoryAgnostic' {
    It 'is true for Scenario 011 (Scoping Criteria Matrix)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 11 | Should -Be $true
    }

    It 'is true for Scenario 015 (SCIM Connector)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 15 | Should -Be $true
    }

    It 'is true for Scenario 016 (JIM SQL Connector matrix)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 16 | Should -Be $true
    }

    It 'is false for a scenario that does use a directory' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 1 | Should -Be $false
    }

    It 'is false for a directory-restricted scenario (it uses a directory, just only one kind)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 17 | Should -Be $false
    }

    It 'is false for $null (the "All" scenario)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber $null | Should -Be $false
    }
}

Describe 'Run-IntegrationTests.ps1 reads the scenario table for every directory type decision' {
    BeforeAll {
        # The runner cannot run without Docker and the lab, so what is proven here is that every decision
        # it takes about which directory types a scenario runs on is wired to the table above: the
        # interactive menu, an explicit -DirectoryType, an unspecified one, the -DirectoryType All
        # expansion and the -Scenario All sweep. There is no second list and no Active Directory
        # special case; Scenarios 024 and 025 are simply the rows whose only type is ActiveDirectory.
        $script:runnerPath = Join-Path $PSScriptRoot '..' 'Run-IntegrationTests.ps1'
        $script:runnerText = Get-Content -LiteralPath $script:runnerPath -Raw

        $tokens = $null
        $parseErrors = $null
        $script:runnerAst = [System.Management.Automation.Language.Parser]::ParseInput($script:runnerText, [ref]$tokens, [ref]$parseErrors)

        $script:runnerCommandNames = @(
            $script:runnerAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true) |
                ForEach-Object { $_.GetCommandName() } |
                Where-Object { $_ }
        )
        $script:runnerThrows = @(
            $script:runnerAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.ThrowStatementAst] }, $true) |
                ForEach-Object { $_.Extent.Text }
        )

        $script:allHandlerIndex = [regex]::Match($script:runnerText, '(?m)^if \(\$DirectoryType -eq "All"\) \{').Index
        $script:sweepIndex = [regex]::Match($script:runnerText, '(?m)^if \(\$Scenario -eq "All"\) \{').Index
        $script:allHandlerText = $script:runnerText.Substring($script:allHandlerIndex, $script:sweepIndex - $script:allHandlerIndex)
        $script:sweepText = $script:runnerText.Substring($script:sweepIndex)
    }

    It 'finds the -DirectoryType All handler and the -Scenario All sweep, in that order' {
        $script:allHandlerIndex | Should -BeGreaterThan 0
        $script:sweepIndex | Should -BeGreaterThan $script:allHandlerIndex
    }

    It 'accepts every directory type the table can return as a -DirectoryType value' {
        $directoryTypeParameter = $script:runnerAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.ParameterAst] -and $node.Name.VariablePath.UserPath -eq 'DirectoryType'
            }, $true) | Select-Object -First 1
        $validateSet = $directoryTypeParameter.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateSet' }
        $accepted = @($validateSet.PositionalArguments | ForEach-Object { $_.Value })

        $accepted | Should -Contain 'All'
        foreach ($number in (1..40) + $null) {
            foreach ($type in @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $number)) {
                $accepted | Should -Contain $type -Because "Scenario $number lists $type"
            }
        }
    }

    It 'no longer calls, or even names, the removed Active Directory only helpers' {
        foreach ($removed in 'Get-ActiveDirectoryOnlyScenarioNumber', 'Test-ActiveDirectoryOnlyScenario', 'Resolve-ActiveDirectoryOnlyScenarioDirectoryType') {
            $script:runnerCommandNames | Should -Not -Contain $removed
            $script:runnerText.Contains($removed) | Should -BeFalse -Because "$removed is gone and a comment naming it would send a reader looking for it"
        }
    }

    Context 'the interactive menu' {
        It 'goes straight to the scenario''s only supported directory type, with no Active Directory branch of its own' {
            ($script:runnerText -match '(?s)\$menuSupportedTypes = @\(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber \$scenarioNumber\)\s*if \(\$menuSupportedTypes\.Count -eq 1\)\s*\{\s*\$DirectoryType = \$menuSupportedTypes\[0\]\s*\}\s*else\s*\{\s*\$DirectoryType = Show-DirectoryTypeMenu') | Should -BeTrue
        }
    }

    Context 'an unsupported directory type' {
        BeforeAll {
            $script:gatingThrows = @($script:runnerThrows | Where-Object { $_.Contains('Rejected -DirectoryType $DirectoryType') })
            $script:gatingIndex = $script:runnerText.IndexOf('$DirectoryType -notin $scenarioSupportedTypes')
        }

        It 'is checked against the table before the -DirectoryType All handler, so the rule is enforced however the type was chosen' {
            $script:gatingIndex | Should -BeGreaterThan 0
            $script:gatingIndex | Should -BeLessThan $script:allHandlerIndex
            $script:runnerText.Substring(0, $script:gatingIndex).Contains('$scenarioSupportedTypes = @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $scenarioNumber)') | Should -BeTrue
        }

        It 'is not checked for "All", which is not a directory type and has its own handler' {
            ($script:runnerText -match '\$DirectoryType -ne "All" -and \$DirectoryType -notin \$scenarioSupportedTypes') | Should -BeTrue
        }

        It 'is refused with a message naming the scenario, the supported types and the rejected type' {
            $script:gatingThrows.Count | Should -Be 1
            $script:gatingThrows[0].Contains('$Scenario') | Should -BeTrue
            $script:gatingThrows[0].Contains('$scenarioSupportedTypes') | Should -BeTrue
            $script:gatingThrows[0].Contains('Rejected -DirectoryType $DirectoryType') | Should -BeTrue
            $script:gatingThrows[0].Contains('Use -DirectoryType') | Should -BeTrue
        }

        It 'is moved to the scenario''s only supported type, with the yellow notice, when nobody asked for a type' {
            ($script:runnerText -match '(?s)\$scenarioSupportedTypes\.Count -eq 1 -and -not \$DirectoryTypeWasExplicitlySet\)\s*\{\s*Write-Host "\$\{YELLOW\}This scenario supports \$\(\$scenarioSupportedTypes\[0\]\) only; using -DirectoryType \$\(\$scenarioSupportedTypes\[0\]\)\.\$\{NC\}"\s*\$DirectoryType = \$scenarioSupportedTypes\[0\]') | Should -BeTrue
        }

        It 'writes its message without an em dash, as every text in this repository must' {
            $script:gatingThrows[0] | Should -Not -Match ([string][char]0x2014)
        }
    }

    Context 'Scenario 023''s preference for OpenLDAP' {
        It 'still defaults to OpenLDAP when no -DirectoryType was given' {
            ($script:runnerText -match '(?s)if \(\$scenarioNumber -eq 23\)\s*\{\s*if \(-not \$DirectoryTypeWasExplicitlySet\)\s*\{\s*Write-Host "\$\{YELLOW\}This scenario defaults to OpenLDAP; using -DirectoryType OpenLDAP\.') | Should -BeTrue
        }

        It 'no longer refuses 389 Directory Server itself, because the table does (see the gating above)' {
            $script:runnerText.Contains('Scenario 023 (Unique Value Generation) supports OpenLDAP and Samba AD only') | Should -BeFalse
        }
    }

    Context '-DirectoryType All' {
        It 'expands to the container directory types, not a hand-copied list' {
            ($script:allHandlerText -match '\$directoryTypesToRun = @\(Get-ContainerDirectoryType\)') | Should -BeTrue
            $script:allHandlerText.Contains('@("SambaAD", "OpenLDAP", "DirectoryServer389")') | Should -BeFalse
        }

        It 'intersects the container directory types with what the scenario supports' {
            ($script:allHandlerText -match '\$restrictedTypes = @\(\$directoryTypesToRun \| Where-Object \{ \$_ -in \$supportedTypes \}\)') | Should -BeTrue
        }

        It 'refuses the scenario when the intersection is empty, saying it runs on the Active Directory lab only, which All never includes' {
            $emptyThrows = @($script:runnerThrows | Where-Object { $_.Contains('Rejected -DirectoryType All') })

            $emptyThrows.Count | Should -Be 1
            $emptyThrows[0].Contains('$Scenario') | Should -BeTrue
            $emptyThrows[0].Contains('Active Directory lab only') | Should -BeTrue
            $emptyThrows[0].Contains('never includes') | Should -BeTrue
            $emptyThrows[0].Contains('Use -DirectoryType') | Should -BeTrue
            $emptyThrows[0] | Should -Not -Match ([string][char]0x2014)
            ($script:allHandlerText -match '\$restrictedTypes\.Count -eq 0\)\s*\{\s*throw') | Should -BeTrue
        }
    }

    Context 'the -Scenario All sweep' {
        It 'skips every scenario whose supported types do not include the sweep''s own directory type' {
            ($script:sweepText -match '\$DirectoryType -notin \(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber \(Get-IntegrationScenarioNumber -Scenario \$_\)\)') | Should -BeTrue
        }

        It 'has no substitution for ActiveDirectory and no lab-only skip of its own' {
            $script:sweepText.Contains('supportDirectoryType') | Should -BeFalse
            $script:sweepText.Contains('Skipping Active Directory lab-only scenario') | Should -BeFalse
            $script:sweepText.Contains('Test-ActiveDirectoryOnlyScenario') | Should -BeFalse
        }
    }
}
