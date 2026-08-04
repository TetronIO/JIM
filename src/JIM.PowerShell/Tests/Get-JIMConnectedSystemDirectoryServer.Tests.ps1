# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Get-JIMConnectedSystemDirectoryServer cmdlet, including its
    Get-JIMConnectedSystemDomainController alias (issue #1167).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemDirectoryServer' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemDirectoryServer
        }

        It 'Should have a mandatory ConnectedSystemId parameter' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have ConnectedSystemId parameter that accepts pipeline by property name' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id as an alias for ConnectedSystemId, so a piped Connected System binds directly' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Aliases | Should -Contain 'Id'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request shape' {

        It 'Calls the directory-servers endpoint for the given Connected System' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }

                Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 42 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/42/directory-servers'
                }
            }
        }

        It 'Binds ConnectedSystemId from a piped Connected System object via the Id alias' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }

                [PSCustomObject]@{ id = 7; name = 'Corp AD' } | Get-JIMConnectedSystemDirectoryServer | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/directory-servers'
                }
            }
        }
    }

    Context 'Response handling' {

        It 'Emits nothing when no domain controllers are discovered' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }

                $result = @(Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 42)

                $result.Count | Should -Be 0
            }
        }

        It 'Emits each discovered directory server individually, unwrapped, for pipeline support' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $dc1 = [PSCustomObject]@{ hostName = 'dc01.corp.local'; site = 'Default-First-Site-Name' }
                $dc2 = [PSCustomObject]@{ hostName = 'dc02.corp.local'; site = 'London' }
                Mock Invoke-JIMApi { @($dc1, $dc2) }

                $result = @(Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 42)

                $result.Count | Should -Be 2
                $result[0].hostName | Should -Be 'dc01.corp.local'
                $result[0].site | Should -Be 'Default-First-Site-Name'
                $result[1].hostName | Should -Be 'dc02.corp.local'
            }
        }

        It 'Writes a non-terminating error when the API call fails, rather than throwing' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { throw "The '400' connector does not support directory server discovery." }

                { Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 42 -ErrorAction SilentlyContinue } | Should -Not -Throw
                Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 42 -ErrorVariable errors -ErrorAction SilentlyContinue
                $errors | Should -Not -BeNullOrEmpty
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMConnectedSystemDirectoryServer -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have a description' {
            $help.Description | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the ConnectedSystemId parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'ConnectedSystemId' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }

        It 'Should document the hostName output field' {
            ($help.returnValues | Out-String) | Should -Match 'hostName'
        }

        It 'Should document the site output field' {
            ($help.returnValues | Out-String) | Should -Match 'site'
        }
    }
}

Describe 'Get-JIMConnectedSystemDomainController alias' {

    It 'Resolves to Get-JIMConnectedSystemDirectoryServer' {
        (Get-Alias Get-JIMConnectedSystemDomainController).ResolvedCommand.Name | Should -Be 'Get-JIMConnectedSystemDirectoryServer'
    }

    It 'Is exported by the module' {
        Get-Command Get-JIMConnectedSystemDomainController -Module JIM -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
    }

    It 'Calls the same directory-servers endpoint as the full cmdlet name' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { @() }

            Get-JIMConnectedSystemDomainController -ConnectedSystemId 99 | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/99/directory-servers'
            }
        }
    }
}
