# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Connected System server-certificate cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemServerCertificate' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemServerCertificate
        }

        It 'Should have a ConnectedSystemId parameter' {
            $command.Parameters['ConnectedSystemId'] | Should -Not -BeNullOrEmpty
        }

        It 'Should accept a piped Connected System' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Aliases | Should -Contain 'Id'
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a SettingValues parameter for settings that have not been saved' {
            $command.Parameters['SettingValues'].ParameterType | Should -Be ([hashtable])
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request Shape' {

        It 'Reads the saved settings with a GET' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ certificate = @{} } }

                Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/42/server-certificate' -and -not $Method
                }
            }
        }

        # Unsaved settings cannot travel on a GET, and they are the usual case: JIM does not save settings
        # that fail validation, and a certificate JIM does not trust is a validation failure.
        It 'Sends settings that have not been saved as a POST' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ certificate = @{} } }

                Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2' } | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/42/server-certificate' -and
                    $Method -eq 'POST' -and
                    $Body.settingValues['40'].stringValue -eq 'https://hr.corp.local/scim/v2'
                }
            }
        }

        It 'Takes the Connected System from the pipeline' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ certificate = @{} } }

                [PSCustomObject]@{ Id = 7 } | Get-JIMConnectedSystemServerCertificate | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/server-certificate'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMConnectedSystemServerCertificate -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Approve-JIMConnectedSystemServerCertificate' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Approve-JIMConnectedSystemServerCertificate
        }

        # The thumbprint is what makes this a decision rather than "trust whatever is there".
        It 'Should require a Thumbprint' {
            $param = $command.Parameters['Thumbprint']
            $attribute = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
            $attribute.Mandatory | Should -Contain $true
        }

        It 'Should support ShouldProcess' {
            $command.Parameters.ContainsKey('WhatIf') | Should -BeTrue
        }

        It 'Should have a PassThru switch' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -Thumbprint 'AABB' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request Shape' {

        It 'Posts the thumbprint and the change reason' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ outcome = 'Trusted' } }

                Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
                    -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01' `
                    -ChangeReason 'Unblocking the connection test.' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/42/server-certificate/trust' -and
                    $Method -eq 'POST' -and
                    $Body.thumbprint -eq '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01' -and
                    $Body.changeReason -eq 'Unblocking the connection test.'
                }
            }
        }

        It 'Sends settings that have not been saved alongside the thumbprint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ outcome = 'Trusted' } }

                Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -Thumbprint 'AABB' `
                    -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2'; 55 = 10 } | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.settingValues['40'].stringValue -eq 'https://hr.corp.local/scim/v2' -and
                    $Body.settingValues['55'].intValue -eq 10
                }
            }
        }

        It 'Trusts nothing when the caller declines the confirmation' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ outcome = 'Trusted' } }

                Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -Thumbprint 'AABB' -WhatIf | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Approve-JIMConnectedSystemServerCertificate -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
