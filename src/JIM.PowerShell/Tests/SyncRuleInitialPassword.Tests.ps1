# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Synchronisation Rule initial password cmdlets (#1121).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMSyncRuleInitialPassword' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMSyncRuleInitialPassword
        }

        It 'Should default to reading by ID' {
            $command.DefaultParameterSet | Should -Be 'ById'
        }

        It 'Should accept a Synchronisation Rule from the pipeline' {
            $command.ParameterSets.Name | Should -Contain 'ByInputObject'
            $param = $command.Parameters['InputObject']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } |
                Should -Not -BeNullOrEmpty
        }
    }

    Context 'Behaviour' {

        It 'Should error rather than call the API when not connected' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                { Get-JIMSyncRuleInitialPassword -Id 5 -ErrorAction Stop } | Should -Throw
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should read the initial password sub-resource of the rule' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { @{ enabled = $true } }

                Get-JIMSyncRuleInitialPassword -Id 5 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/sync-rules/5/initial-password' -and $Method -eq 'GET'
                }
            }
        }
    }
}

Describe 'Set-JIMSyncRuleInitialPassword' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMSyncRuleInitialPassword
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should constrain <Parameter> to the values the API accepts' -ForEach @(
            @{ Parameter = 'Source'; Expected = @('Discovered', 'Custom') }
            @{ Parameter = 'Style'; Expected = @('RandomCharacters', 'Words', 'Pronounceable') }
            @{ Parameter = 'WordSeparator'; Expected = @('None', 'Hyphen', 'FullStop', 'Underscore', 'Digit', 'RandomSymbol') }
            @{ Parameter = 'WordCapitalisation'; Expected = @('Lowercase', 'EachWord', 'Uppercase', 'FirstWordOnly', 'RandomWord') }
            @{ Parameter = 'ExpiryBehaviour'; Expected = @('RequireChangeAtNextSignIn', 'ExpiresAccordingToTargetPolicy', 'NeverExpires') }
        ) {
            $validateSet = $command.Parameters[$Parameter].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Be $Expected
        }
    }

    Context 'Behaviour' {

        It 'Should send only what was asked for' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                $sent = $null
                Mock Invoke-JIMApi { $script:sent = $Body; @{ enabled = $true } }

                Set-JIMSyncRuleInitialPassword -Id 5 -Enable -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'PUT' -and
                    $Endpoint -eq '/api/v1/synchronisation/sync-rules/5/initial-password' -and
                    $Body.enabled -eq $true -and
                    -not $Body.ContainsKey('source') -and
                    -not $Body.ContainsKey('customPolicy')
                }
            }
        }

        It 'Should reject -Enable and -Disable together' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { throw 'the API must not be called' }

                { Set-JIMSyncRuleInitialPassword -Id 5 -Enable -Disable -Confirm:$false -ErrorAction Stop } | Should -Throw
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should warn and send nothing when there is nothing to change' {
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi { throw 'the API must not be called' }
                Mock Write-Warning { }

                Set-JIMSyncRuleInitialPassword -Id 5 -Confirm:$false

                Should -Invoke Write-Warning -Times 1
                Should -Invoke Invoke-JIMApi -Times 0
            }
        }

        It 'Should read the stored settings and send the whole policy when one generator setting changes' {
            # The generator settings only make sense as a set, so a partial send would reset the fields left
            # out to the API's defaults. This is the test that proves the cmdlet reads before it writes.
            InModuleScope JIM {
                $script:JIMConnection = @{ Url = 'https://jim.example.test' }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'GET' } -MockWith {
                    @{ customPolicy = [PSCustomObject]@{ style = 'Words'; wordCount = 4; appendedDigitCount = 2; permittedSymbols = '!#$' } }
                }
                Mock Invoke-JIMApi -ParameterFilter { $Method -eq 'PUT' } -MockWith { @{ enabled = $true } }

                Set-JIMSyncRuleInitialPassword -Id 5 -WordCount 6 -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter {
                    $Method -eq 'PUT' -and
                    $Body.customPolicy.wordCount -eq 6 -and
                    $Body.customPolicy.appendedDigitCount -eq 2 -and
                    $Body.customPolicy.permittedSymbols -eq '!#$'
                }
            }
        }
    }
}
