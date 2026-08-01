# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Synchronisation Rule cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMSyncRule' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMSyncRule
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
            $command = Get-Command Get-JIMSyncRule
        }

        It 'Should have Id parameter' {
            $command.Parameters['Id'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have ConnectedSystemId parameter that accepts pipeline by property name' {
            $param = $command.Parameters['ConnectedSystemId']
            $param | Should -Not -BeNullOrEmpty
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have ConnectedSystemName parameter' {
            $command.Parameters['ConnectedSystemName'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Direction parameter with ValidateSet' {
            $param = $command.Parameters['Direction']
            $param | Should -Not -BeNullOrEmpty
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Import'
            $validateSet.ValidValues | Should -Contain 'Export'
        }

        It 'Should have an ActionType parameter with ValidateSet' {
            $param = $command.Parameters['ActionType']
            $param | Should -Not -BeNullOrEmpty
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Projects'
            $validateSet.ValidValues | Should -Contain 'Provisions'
            $validateSet.ValidValues | Should -Contain 'FlowOnly'
        }

        It 'Should have a Status parameter with ValidateSet' {
            $param = $command.Parameters['Status']
            $param | Should -Not -BeNullOrEmpty
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Enabled'
            $validateSet.ValidValues | Should -Contain 'Disabled'
        }

        It 'Should accept multiple values for Direction, ActionType and Status' {
            $command.Parameters['Direction'].ParameterType | Should -Be ([string[]])
            $command.Parameters['ActionType'].ParameterType | Should -Be ([string[]])
            $command.Parameters['Status'].ParameterType | Should -Be ([string[]])
        }
    }

    Context 'Filter composition' {

        It 'Sends no filter facets in the query string when none are specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                Get-JIMSyncRule | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -notmatch 'directions=' -and
                    $Endpoint -notmatch 'actionTypes=' -and
                    $Endpoint -notmatch 'statuses=' -and
                    $Endpoint -notmatch 'connectedSystemIds=' -and
                    $Endpoint -notmatch 'search='
                }
            }
        }

        It 'Sends the Direction facet as a repeated query parameter' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                Get-JIMSyncRule -Direction Import, Export | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'directions=Import' -and $Endpoint -match 'directions=Export'
                }
            }
        }

        It 'Sends the ActionType facet in the query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                Get-JIMSyncRule -ActionType Provisions | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'actionTypes=Provisions'
                }
            }
        }

        It 'Sends the Status facet in the query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                Get-JIMSyncRule -Status Disabled | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'statuses=Disabled'
                }
            }
        }

        It 'Sends the Connected System facet in the query string rather than filtering client-side' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                Get-JIMSyncRule -ConnectedSystemId 7 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'connectedSystemIds=7'
                }
            }
        }

        It 'Combines facets with the name filter, which stays client-side for wildcard support' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $rules = @(
                    [PSCustomObject]@{ id = 1; name = 'HR Inbound'; connectedSystemId = 1 },
                    [PSCustomObject]@{ id = 2; name = 'Payroll Inbound'; connectedSystemId = 1 }
                )
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = $rules; hasNextPage = $false; totalPages = 1 } }

                $result = @(Get-JIMSyncRule -Direction Import -Name 'HR*')

                $result.Count | Should -Be 1
                $result[0].name | Should -Be 'HR Inbound'
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'directions=Import'
                }
            }
        }

        It 'Pages through every result so a filtered list is never silently truncated' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Endpoint -match 'page=1') {
                        [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'Rule 1' }); hasNextPage = $true; totalPages = 2 }
                    }
                    else {
                        [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 2; name = 'Rule 2' }); hasNextPage = $false; totalPages = 2 }
                    }
                }

                $result = @(Get-JIMSyncRule)

                $result.Count | Should -Be 2
                Should -Invoke Invoke-JIMApi -Times 2 -Exactly
            }
        }
    }

    Context 'Pipeline binding' {

        It 'Binds a piped Connected System (which exposes Id, not ConnectedSystemId), as documented' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                # As returned by Get-JIMConnectedSystem: a PSCustomObject exposing Id.
                $connectedSystem = [PSCustomObject]@{ Id = 4; Name = 'HR System' }
                $connectedSystem | Get-JIMSyncRule | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'connectedSystemIds=4'
                }
            }
        }

        It 'Prefers an explicit ConnectedSystemId over the piped object' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                $connectedSystem = [PSCustomObject]@{ Id = 4; Name = 'HR System' }
                $connectedSystem | Get-JIMSyncRule -ConnectedSystemId 9 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'connectedSystemIds=9'
                }
            }
        }

        It 'Still binds an object exposing ConnectedSystemId by property name' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                $syncRule = [PSCustomObject]@{ Id = 1; ConnectedSystemId = 6 }
                $syncRule | Get-JIMSyncRule | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'connectedSystemIds=6'
                }
            }
        }

        It 'Applies the facet filters to a piped Connected System' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1 } }

                $connectedSystem = [PSCustomObject]@{ Id = 4; Name = 'HR System' }
                $connectedSystem | Get-JIMSyncRule -Direction Export -Status Enabled | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'connectedSystemIds=4' -and
                    $Endpoint -match 'directions=Export' -and
                    $Endpoint -match 'statuses=Enabled'
                }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMSyncRule -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMSyncRule -Full
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

Describe 'New-JIMSyncRule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMSyncRule
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have an optional Description parameter' {
            $param = $command.Parameters['Description']
            $param | Should -Not -BeNullOrEmpty
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -BeNullOrEmpty
        }

        It 'Should have a mandatory ConnectedSystemId parameter in ById set' {
            $param = $command.Parameters['ConnectedSystemId']
            $paramAttr = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and
                $_.Mandatory -and
                $_.ParameterSetName -eq 'ById'
            }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It 'Should have a ConnectedSystemName parameter' {
            $command.Parameters['ConnectedSystemName'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory ConnectedSystemObjectTypeId parameter' {
            $param = $command.Parameters['ConnectedSystemObjectTypeId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory MetaverseObjectTypeId parameter' {
            $param = $command.Parameters['MetaverseObjectTypeId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Direction parameter' {
            $param = $command.Parameters['Direction']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Direction parameter with ValidateSet' {
            $param = $command.Parameters['Direction']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Import'
            $validateSet.ValidValues | Should -Contain 'Export'
        }

        It 'Should have ProjectToMetaverse switch parameter' {
            $command.Parameters['ProjectToMetaverse'].SwitchParameter | Should -BeTrue
        }

        It 'Should have ProvisionToConnectedSystem switch parameter' {
            $command.Parameters['ProvisionToConnectedSystem'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have OutboundDeprovisionAction parameter with ValidateSet' {
            $param = $command.Parameters['OutboundDeprovisionAction']
            $param | Should -Not -BeNullOrEmpty
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Disconnect'
            $validateSet.ValidValues | Should -Contain 'Delete'
        }
    }

    Context 'Request body composition' {

        It 'Sends outboundDeprovisionAction in the POST body when -OutboundDeprovisionAction is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMSyncRule -Name 'Test' -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Export -OutboundDeprovisionAction Delete -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.outboundDeprovisionAction -eq 'Delete'
                }
            }
        }

        It 'Omits outboundDeprovisionAction from the POST body when not specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMSyncRule -Name 'Test' -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Export -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('outboundDeprovisionAction')
                }
            }
        }

        It 'Sends description in the POST body when -Description is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMSyncRule -Name 'Test' -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -Description 'Imports users from the HR system' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.description -eq 'Imports users from the HR system'
                }
            }
        }

        It 'Omits description from the POST body when -Description is not specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMSyncRule -Name 'Test' -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('description')
                }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { New-JIMSyncRule -Name "Test" -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMSyncRule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Set-JIMSyncRule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMSyncRule
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have an optional Description parameter' {
            $param = $command.Parameters['Description']
            $param | Should -Not -BeNullOrEmpty
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -BeNullOrEmpty
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
        }
    }

    Context 'Request body composition' {

        It 'Sends description in the PUT body when -Description is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                Set-JIMSyncRule -Id 1 -Description 'Imports users from the HR system' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.description -eq 'Imports users from the HR system'
                }
            }
        }

        It 'Sends an empty description in the PUT body when -Description is $null (clears the value)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                # the binder coerces $null to '' for [string] parameters, so this and -Description ''
                # are equivalent; $null is the documented convention for clearing
                Set-JIMSyncRule -Id 1 -Description $null -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.ContainsKey('description') -and $Body.description -eq ''
                }
            }
        }

        It 'Sends an empty description in the PUT body when -Description is an explicit empty string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                Set-JIMSyncRule -Id 1 -Description '' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.ContainsKey('description') -and $Body.description -eq ''
                }
            }
        }

        It 'Omits description from the PUT body when -Description is not specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                Set-JIMSyncRule -Id 1 -Name 'Updated Name' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('description')
                }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMSyncRule -Id 1 -Name "Test" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMSyncRule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Remove-JIMSyncRule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMSyncRule
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
            { Remove-JIMSyncRule -Id 1 -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMSyncRule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
