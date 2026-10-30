# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Get-JIMConnectedSystemCatalogue cmdlet (issue #2570).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemCatalogue' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemCatalogue
        }

        It 'Should have a mandatory ConnectedSystemId parameter that a piped Connected System binds through its Id' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
            $param.Aliases | Should -Contain 'Id'
        }

        It 'Should not allow -Name and -Statement together' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                { Get-JIMConnectedSystemCatalogue -ConnectedSystemId 1 -Name 'EMPLOYEES' -Statement 'SELECT 1' -ErrorAction Stop } | Should -Throw '*Parameter set cannot be resolved*'
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should error when not connected' {
            { Get-JIMConnectedSystemCatalogue -ConnectedSystemId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request shape' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            }
        }

        It 'Lists the catalogue by default, one object per table or view' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { @([PSCustomObject]@{ name = 'EMPLOYEES' }, [PSCustomObject]@{ name = 'V_EMPLOYEES' }) }

                $result = @(Get-JIMConnectedSystemCatalogue -ConnectedSystemId 42)

                $result.Count | Should -Be 2
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/42/catalogue' -and $Method -ne 'POST'
                }
            }
        }

        It 'Binds ConnectedSystemId from a piped Connected System object' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { @() }

                [PSCustomObject]@{ id = 7; name = 'HR Database' } | Get-JIMConnectedSystemCatalogue | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/catalogue'
                }
            }
        }

        It 'Describes one object by schema and name, encoding both' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'EMPLOYEE LIST' } }

                Get-JIMConnectedSystemCatalogue -ConnectedSystemId 42 -Schema 'HR' -Name 'EMPLOYEE LIST' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/42/catalogue/object?schema=HR&name=EMPLOYEE%20LIST'
                }
            }
        }

        It 'Describes one object by name alone, leaving the schema out' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { [PSCustomObject]@{ name = 'EMPLOYEES' } }

                Get-JIMConnectedSystemCatalogue -ConnectedSystemId 42 -Name 'EMPLOYEES' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/42/catalogue/object?name=EMPLOYEES'
                }
            }
        }

        It 'Describes a statement by POSTing it in the body, never in the URL' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { [PSCustomObject]@{ kind = 'Statement' } }

                Get-JIMConnectedSystemCatalogue -ConnectedSystemId 42 -Statement 'SELECT EMPLOYEE_ID FROM HR.EMPLOYEES' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/42/catalogue/statement' -and
                    $Method -eq 'POST' -and
                    $Body.statement -eq 'SELECT EMPLOYEE_ID FROM HR.EMPLOYEES'
                }
            }
        }
    }
}
