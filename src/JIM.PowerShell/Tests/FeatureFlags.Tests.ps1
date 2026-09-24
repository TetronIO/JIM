# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for feature flag cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMFeature' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMFeature
        }

        It 'Should have a Name parameter' {
            $command.Parameters['Name'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have an IncludeInDevelopment switch parameter' {
            $command.Parameters['IncludeInDevelopment'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMFeature -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request shape' {

        It 'Defaults includeInDevelopment to false' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }

                Get-JIMFeature | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/features?includeInDevelopment=false'
                }
            }
        }

        It 'Passes includeInDevelopment=true when requested' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }

                Get-JIMFeature -IncludeInDevelopment | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/features?includeInDevelopment=true'
                }
            }
        }

        It 'Filters the response to the named flag' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ key = 'Features.A'; enabled = $true }
                        [PSCustomObject]@{ key = 'Features.B'; enabled = $false }
                    )
                }

                $result = Get-JIMFeature -Name 'Features.B'

                $result.key | Should -Be 'Features.B'
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMFeature -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Enable-JIMFeature' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Enable-JIMFeature
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline input by property name for Name' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should alias Name to Key, so Get-JIMFeature output pipes straight in' {
            $command.Parameters['Name'].Aliases | Should -Contain 'Key'
        }

        It 'Should have an AllowInDevelopment switch parameter' {
            $command.Parameters['AllowInDevelopment'].SwitchParameter | Should -BeTrue
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
            { Enable-JIMFeature -Name 'Features.UniqueValueGeneration' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request shape' {

        It 'Sends enabled=true and allowInDevelopment=false by default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ key = 'Features.A' } }

                Enable-JIMFeature -Name 'Features.A' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/features/Features.A' -and $Method -eq 'PUT' -and
                    $Body.enabled -eq $true -and $Body.allowInDevelopment -eq $false
                }
            }
        }

        It 'Sends allowInDevelopment=true when specified, as scenario setup does' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ key = 'Features.UniqueValueGeneration' } }

                Enable-JIMFeature -Name 'Features.UniqueValueGeneration' -AllowInDevelopment | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/features/Features.UniqueValueGeneration' -and
                    $Body.allowInDevelopment -eq $true
                }
            }
        }

        It 'Accepts piped Get-JIMFeature output by its Key property' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ key = $Endpoint } }

                [PSCustomObject]@{ Key = 'Features.A' } | Enable-JIMFeature | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/features/Features.A'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Enable-JIMFeature -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Disable-JIMFeature' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Disable-JIMFeature
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
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
            { Disable-JIMFeature -Name 'Features.UniqueValueGeneration' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request shape' {

        It 'Sends enabled=false and no allowInDevelopment field' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ key = 'Features.A' } }

                Disable-JIMFeature -Name 'Features.A' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/features/Features.A' -and $Method -eq 'PUT' -and
                    $Body.enabled -eq $false -and -not $Body.ContainsKey('allowInDevelopment')
                }
            }
        }

        It 'Disables every piped flag by its Key property' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ key = $Endpoint } }

                $flags = @(
                    [PSCustomObject]@{ Key = 'Features.A' }
                    [PSCustomObject]@{ Key = 'Features.B' }
                )
                $flags | Disable-JIMFeature | Out-Null

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Disable-JIMFeature -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
