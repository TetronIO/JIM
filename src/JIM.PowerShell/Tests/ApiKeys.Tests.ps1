# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for API Key cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMApiKey' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMApiKey
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMApiKey
        }

        It 'Should have Id parameter that accepts GUID' {
            $param = $command.Parameters['Id']
            $param.ParameterType.Name | Should -Be 'Guid'
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMApiKey -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMApiKey -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'New-JIMApiKey' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMApiKey
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Description parameter' {
            $command.Parameters['Description'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a RoleIds parameter' {
            $command.Parameters['RoleIds'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have an ExpiresAt parameter' {
            $command.Parameters['ExpiresAt'] | Should -Not -BeNullOrEmpty
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
            { New-JIMApiKey -Name "Test" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMApiKey -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Set-JIMApiKey' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMApiKey
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Enable switch parameter' {
            $command.Parameters['Enable'].SwitchParameter | Should -BeTrue
        }

        It 'Should have Disable switch parameter' {
            $command.Parameters['Disable'].SwitchParameter | Should -BeTrue
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
            { Set-JIMApiKey -Id ([Guid]::NewGuid()) -Name "Test" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'The request it builds' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                $script:testKeyId = [guid]'11111111-1111-1111-1111-111111111111'
            }
        }

        It 'Keeps a single explicit -RoleIds value an array, so it serialises as one and not as a scalar' {
            InModuleScope JIM {
                # Assigning from an if-expression enumerates its output, which collapses a
                # one-element array to a scalar Int32; ConvertTo-Json then sends {"roleIds":3}
                # and the API's List<int> binding rejects it. The value must still be an array
                # at the serialisation boundary (#1531).
                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') { $script:capturedBody = $Body; return }
                    [PSCustomObject]@{
                        id = $script:testKeyId; name = 'Automation Key'; description = 'existing'
                        roles = @([PSCustomObject]@{ id = 1 }, [PSCustomObject]@{ id = 2 })
                        isEnabled = $true; expiresAt = $null
                    }
                }

                Set-JIMApiKey -Id $script:testKeyId -RoleIds 3 -Confirm:$false

                $script:capturedBody.roleIds -is [System.Collections.ICollection] | Should -BeTrue
                (ConvertTo-Json -InputObject $script:capturedBody.roleIds -Compress) | Should -Be '[3]'
            }
        }

        It 'Preserves an existing single role as an array when the update does not touch roles' {
            InModuleScope JIM {
                # An API Key with exactly one role is the common case, so a rename that should
                # preserve the role must send [7], not the scalar 7 (#1531).
                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') { $script:capturedBody = $Body; return }
                    [PSCustomObject]@{
                        id = $script:testKeyId; name = 'Automation Key'; description = 'existing'
                        roles = @([PSCustomObject]@{ id = 7 })
                        isEnabled = $true; expiresAt = $null
                    }
                }

                Set-JIMApiKey -Id $script:testKeyId -Name 'Renamed Key' -Confirm:$false

                $script:capturedBody.roleIds -is [System.Collections.ICollection] | Should -BeTrue
                (ConvertTo-Json -InputObject $script:capturedBody.roleIds -Compress) | Should -Be '[7]'
            }
        }

        It 'Preserves zero roles as an empty JSON array, not null' {
            InModuleScope JIM {
                # The same if-expression enumeration turns @() into $null, which serialises as
                # {"roleIds":null} rather than the empty set the key actually holds (#1531).
                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') { $script:capturedBody = $Body; return }
                    [PSCustomObject]@{
                        id = $script:testKeyId; name = 'Automation Key'; description = 'existing'
                        roles = @()
                        isEnabled = $true; expiresAt = $null
                    }
                }

                Set-JIMApiKey -Id $script:testKeyId -Name 'Renamed Key' -Confirm:$false

                $script:capturedBody.ContainsKey('roleIds') | Should -BeTrue
                (ConvertTo-Json -InputObject $script:capturedBody.roleIds -Compress) | Should -Be '[]'
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMApiKey -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Remove-JIMApiKey' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMApiKey
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have InputObject parameter that accepts pipeline input' {
            $param = $command.Parameters['InputObject']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Remove-JIMApiKey -Id ([Guid]::NewGuid()) -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMApiKey -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
