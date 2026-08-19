# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMConnectedSystemObjectType cmdlet.

.DESCRIPTION
    Concentrates on the internal object type default, which is the one place this cmdlet withholds something the API
    returned. Scripted callers must get the same view of a Connected System's schema as the portal's schema screen,
    and must be able to ask for the rest.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemObjectType' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemObjectType
        }

        It 'Should have a mandatory ConnectedSystemId parameter' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should alias ConnectedSystemId as Id, so a piped Connected System binds by its own Id property' {
            $command.Parameters['ConnectedSystemId'].Aliases | Should -Contain 'Id'
        }

        It 'Should have an IncludeInternal switch' {
            $command.Parameters['IncludeInternal'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMConnectedSystemObjectType -ConnectedSystemId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Internal object types' {

        It 'Omits internal object types by default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ name = 'inetOrgPerson'; isInternal = $false; selected = $false },
                        [PSCustomObject]@{ name = 'olcGlobal'; isInternal = $true; selected = $false },
                        [PSCustomObject]@{ name = 'auditAdd'; isInternal = $true; selected = $false }
                    )
                }

                $result = @(Get-JIMConnectedSystemObjectType -ConnectedSystemId 7)

                $result.Count | Should -Be 1
                $result[0].name | Should -Be 'inetOrgPerson'
            }
        }

        It 'Returns internal object types when asked for them' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ name = 'inetOrgPerson'; isInternal = $false; selected = $false },
                        [PSCustomObject]@{ name = 'olcGlobal'; isInternal = $true; selected = $false }
                    )
                }

                $result = @(Get-JIMConnectedSystemObjectType -ConnectedSystemId 7 -IncludeInternal)

                $result.Count | Should -Be 2
                ($result | Where-Object { $_.name -eq 'olcGlobal' }) | Should -Not -BeNullOrEmpty
            }
        }

        It 'Always returns an internal object type the administrator has selected' {
            InModuleScope JIM {
                # Withholding a type someone deliberately chose to manage would make it invisible to the automation
                # that manages it, which is worse than the noise the default exists to remove.
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ name = 'auditAdd'; isInternal = $true; selected = $true },
                        [PSCustomObject]@{ name = 'olcGlobal'; isInternal = $true; selected = $false }
                    )
                }

                $result = @(Get-JIMConnectedSystemObjectType -ConnectedSystemId 7)

                $result.Count | Should -Be 1
                $result[0].name | Should -Be 'auditAdd'
            }
        }

        It 'Passes a Reference attribute''s declared target Object Type through untouched' {
            InModuleScope JIM {
                # The declared target decides which Object Type a reference resolves within (#1285);
                # scripted callers need the same read-only view of it as the portal's Schema tab.
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{
                            name = 'Person'
                            selected = $true
                            attributes = @(
                                [PSCustomObject]@{ name = 'DEPARTMENT_ID'; type = 'Reference'; referencedObjectTypeId = 9; referencedObjectTypeName = 'Department' },
                                [PSCustomObject]@{ name = 'MANAGER'; type = 'Reference'; referencedObjectTypeId = $null; referencedObjectTypeName = $null }
                            )
                        }
                    )
                }

                $result = @(Get-JIMConnectedSystemObjectType -ConnectedSystemId 7)

                $declared = $result[0].attributes | Where-Object { $_.name -eq 'DEPARTMENT_ID' }
                $declared.referencedObjectTypeId | Should -Be 9
                $declared.referencedObjectTypeName | Should -Be 'Department'
                $undeclared = $result[0].attributes | Where-Object { $_.name -eq 'MANAGER' }
                $undeclared.referencedObjectTypeName | Should -BeNullOrEmpty
            }
        }

        It 'Returns every object type from a Connected System that classifies nothing' {
            InModuleScope JIM {
                # The File and SCIM connectors report no classification at all, so nothing may be filtered out.
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ name = 'User' },
                        [PSCustomObject]@{ name = 'Group' }
                    )
                }

                $result = @(Get-JIMConnectedSystemObjectType -ConnectedSystemId 7)

                $result.Count | Should -Be 2
            }
        }
    }

    Context 'Requests the object types endpoint' {

        It 'Calls the connected-systems/{id}/object-types endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }

                Get-JIMConnectedSystemObjectType -ConnectedSystemId 7 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/object-types'
                }
            }
        }

        It 'Accepts a Connected System piped by its Id property' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }

                [PSCustomObject]@{ Id = 9 } | Get-JIMConnectedSystemObjectType | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/9/object-types'
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMConnectedSystemObjectType -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should document the IncludeInternal parameter' {
            ($help.parameters.parameter | Where-Object { $_.name -eq 'IncludeInternal' }).description.Text | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
