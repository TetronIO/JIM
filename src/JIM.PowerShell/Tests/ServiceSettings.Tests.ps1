# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Service Setting cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Reset-JIMServiceSetting' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Reset-JIMServiceSetting
        }

        It 'Should have a mandatory Key parameter' {
            $param = $command.Parameters['Key']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline input by property name for Key' {
            $param = $command.Parameters['Key']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Reset-JIMServiceSetting -Key 'Sync.PageSize' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Pipeline binding' {

        It 'Resets every piped setting by property name (as documented: Get-JIMServiceSetting | Where-Object { $_.IsOverridden } | Reset-JIMServiceSetting)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ key = $Endpoint } }

                $overriddenSettings = @(
                    [PSCustomObject]@{ Key = 'ChangeTracking.CsoChanges.Enabled'; IsOverridden = $true }
                    [PSCustomObject]@{ Key = 'Sync.PageSize'; IsOverridden = $true }
                )

                $overriddenSettings | Reset-JIMServiceSetting | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/service-settings/ChangeTracking.CsoChanges.Enabled' -and $Method -eq 'DELETE'
                }
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/service-settings/Sync.PageSize' -and $Method -eq 'DELETE'
                }
                Should -Invoke Invoke-JIMApi -Times 2 -Exactly
            }
        }

        It 'Resets a single setting piped in on its own' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ key = $Endpoint } }

                [PSCustomObject]@{ Key = 'History.RetentionPeriod' } | Reset-JIMServiceSetting | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/service-settings/History.RetentionPeriod' -and $Method -eq 'DELETE'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Reset-JIMServiceSetting -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
