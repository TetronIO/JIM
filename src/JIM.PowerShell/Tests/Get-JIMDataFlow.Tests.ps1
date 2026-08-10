# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Get-JIMDataFlow cmdlet (#1199).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMDataFlow' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMDataFlow
        }

        It 'Should accept only Import or Export for Direction' {
            $validateSet = $command.Parameters['Direction'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('Import', 'Export')
        }

        It 'Should accept a Connected System piped by property name' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should declare SyncRuleName as supporting wildcards' {
            $command.Parameters['SyncRuleName'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.SupportsWildcardsAttribute] } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMDataFlow -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Requests the data-flows endpoint' {

        It 'Calls the data-flows endpoint with no filters by default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMDataFlow | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/data-flows?page=1&pageSize=100'
                }
            }
        }

        It 'Passes every filter through to the endpoint' {
            # The filters are the cmdlet's whole contract; one silently dropped would return a
            # plausible-looking but wrong answer, which is worse than an error.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMDataFlow -Direction Import -ConnectedSystemId 3 -MetaverseObjectTypeId 4 `
                    -ConnectedSystemObjectTypeId 5 -MetaverseAttributeId 6 -ConnectedSystemAttributeId 7 `
                    -MultipleContributorsOnly -Search 'department' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*direction=Import*' -and
                    $Endpoint -like '*connectedSystemId=3*' -and
                    $Endpoint -like '*metaverseObjectTypeId=4*' -and
                    $Endpoint -like '*connectedSystemObjectTypeId=5*' -and
                    $Endpoint -like '*metaverseAttributeId=6*' -and
                    $Endpoint -like '*connectedSystemAttributeId=7*' -and
                    $Endpoint -like '*multipleContributorsOnly=true*' -and
                    $Endpoint -like '*search=department*'
                }
            }
        }

        It 'Escapes a search term so a space or ampersand cannot break the query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMDataFlow -Search 'Job Title' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*search=Job%20Title*'
                }
            }
        }

        It 'Emits each flow as a separate pipeline object' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        items = @(
                            [PSCustomObject]@{ syncRuleMappingId = 1; syncRuleName = 'HR Import'; priority = 1 },
                            [PSCustomObject]@{ syncRuleMappingId = 2; syncRuleName = 'AD Export'; enforceState = $true }
                        )
                        hasNextPage = $false
                    }
                }

                $result = @(Get-JIMDataFlow)

                $result.Count | Should -Be 2
                $result[0].syncRuleName | Should -Be 'HR Import'
                $result[1].enforceState | Should -BeTrue
            }
        }

        It 'Filters by Synchronisation Rule name client-side, with wildcards' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        items = @(
                            [PSCustomObject]@{ syncRuleMappingId = 1; syncRuleName = 'HR Import' },
                            [PSCustomObject]@{ syncRuleMappingId = 2; syncRuleName = 'AD Export' }
                        )
                        hasNextPage = $false
                    }
                }

                $result = @(Get-JIMDataFlow -SyncRuleName 'HR*')

                $result.Count | Should -Be 1
                $result[0].syncRuleName | Should -Be 'HR Import'
            }
        }

        It 'Pages through the whole result set rather than stopping at the first page' {
            # Truncating would make the filters look as though they had excluded flows they did not.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Endpoint -like '*page=1*') {
                        [PSCustomObject]@{ items = @([PSCustomObject]@{ syncRuleMappingId = 1 }); hasNextPage = $true; totalPages = 2 }
                    }
                    else {
                        [PSCustomObject]@{ items = @([PSCustomObject]@{ syncRuleMappingId = 2 }); hasNextPage = $false; totalPages = 2 }
                    }
                }

                $result = @(Get-JIMDataFlow)

                $result.Count | Should -Be 2
                Should -Invoke Invoke-JIMApi -Times 2 -Exactly
            }
        }

        It 'Accepts a Connected System piped by its Id property' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                [PSCustomObject]@{ Id = 9 } | Get-JIMDataFlow | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*connectedSystemId=9*'
                }
            }
        }
    }

    Context 'Documentation' {

        It 'Documents every parameter' {
            $help = Get-Help Get-JIMDataFlow -Full
            $documented = $help.parameters.parameter.name
            foreach ($name in @('Direction', 'ConnectedSystemId', 'ConnectedSystemName', 'MetaverseObjectTypeId',
                    'ConnectedSystemObjectTypeId', 'MetaverseAttributeId', 'ConnectedSystemAttributeId',
                    'MultipleContributorsOnly', 'Search', 'SyncRuleName')) {
                $documented | Should -Contain $name
            }
        }

        It 'States the expression limitation on the attribute filters' {
            $help = Get-Help Get-JIMDataFlow -Full
            $help.description.Text | Should -Match 'null rather than defaulted'
            ($help.parameters.parameter | Where-Object { $_.name -eq 'MetaverseAttributeId' }).description.Text |
                Should -Match 'expression'
        }
    }
}
