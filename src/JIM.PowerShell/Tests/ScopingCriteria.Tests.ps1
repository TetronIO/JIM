# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Connected System attribute name-resolution path of the
    Synchronisation Rule scoping criterion cmdlets (New-/Set-JIMScopingCriterion).
.DESCRIPTION
    Regression coverage for issue #930: -ConnectedSystemAttributeName resolved the
    attribute id by calling a single-object-type GET endpoint that does not exist,
    so the name path always failed. Resolution must use the object-types list
    endpoint (GET /api/v1/synchronisation/connected-systems/{id}/object-types) and
    filter client-side by object type id, mirroring Get-JIMConnectedSystemObjectType.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'New-JIMScopingCriterion -ConnectedSystemAttributeName' {

    It 'Resolves the attribute name to its id via the object-types list endpoint and sends it in the create body' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

            Mock Invoke-JIMApi {
                if ($Endpoint -eq '/api/v1/synchronisation/sync-rules/1') {
                    [PSCustomObject]@{
                        id                          = 1
                        direction                   = 'Import'
                        connectedSystemId           = 7
                        connectedSystemObjectTypeId = 42
                    }
                }
                elseif ($Endpoint -eq '/api/v1/synchronisation/connected-systems/7/object-types') {
                    @(
                        [PSCustomObject]@{
                            id         = 42
                            name       = 'user'
                            attributes = @(
                                [PSCustomObject]@{ id = 100; name = 'employeeNumber' }
                                [PSCustomObject]@{ id = 101; name = 'displayName' }
                            )
                        }
                        [PSCustomObject]@{ id = 43; name = 'group'; attributes = @() }
                    )
                }
                else {
                    [PSCustomObject]@{ id = 999 }
                }
            }

            New-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -ConnectedSystemAttributeName 'employeeNumber' -ComparisonType NotEquals -StringValue 'S14-4' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/object-types'
            }
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'POST' -and
                $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/scoping-criteria/5/criteria' -and
                $Body.connectedSystemAttributeId -eq 100
            }
        }
    }

    It 'Writes an error and does not create a criterion when the attribute name is not found' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

            Mock Invoke-JIMApi {
                if ($Endpoint -eq '/api/v1/synchronisation/sync-rules/1') {
                    [PSCustomObject]@{ id = 1; direction = 'Import'; connectedSystemId = 7; connectedSystemObjectTypeId = 42 }
                }
                elseif ($Endpoint -eq '/api/v1/synchronisation/connected-systems/7/object-types') {
                    @([PSCustomObject]@{ id = 42; name = 'user'; attributes = @([PSCustomObject]@{ id = 100; name = 'employeeNumber' }) })
                }
                else {
                    [PSCustomObject]@{ id = 999 }
                }
            }

            New-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -ConnectedSystemAttributeName 'doesNotExist' -ComparisonType Equals -StringValue 'x' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

            Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'POST' }
        }
    }
}

Describe 'Set-JIMScopingCriterion -ConnectedSystemAttributeName' {

    It 'Resolves the attribute name to its id via the object-types list endpoint and sends it in the update body' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

            Mock Invoke-JIMApi {
                if ($Endpoint -eq '/api/v1/synchronisation/sync-rules/1') {
                    [PSCustomObject]@{ id = 1; direction = 'Import'; connectedSystemId = 7; connectedSystemObjectTypeId = 42 }
                }
                elseif ($Endpoint -eq '/api/v1/synchronisation/connected-systems/7/object-types') {
                    @([PSCustomObject]@{ id = 42; name = 'user'; attributes = @([PSCustomObject]@{ id = 100; name = 'employeeNumber' }) })
                }
                else {
                    [PSCustomObject]@{ id = 999 }
                }
            }

            Set-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -CriterionId 3 -ConnectedSystemAttributeName 'employeeNumber' -ComparisonType NotEquals -StringValue 'S14-4' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/object-types'
            }
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PUT' -and
                $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/scoping-criteria/5/criteria/3' -and
                $Body.connectedSystemAttributeId -eq 100
            }
        }
    }
}
