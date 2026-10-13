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

Describe 'New-JIMScopingCriterion -DecimalValue' {

    It 'Sends decimalValue as a decimal in the create body when -DecimalValue is bound' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 999 } }

            New-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -MetaverseAttributeId 15 -ComparisonType GreaterThanOrEquals -DecimalValue 12345.678d -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'POST' -and
                $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/scoping-criteria/5/criteria' -and
                $Body.decimalValue -is [decimal] -and
                $Body.decimalValue -eq 12345.678d
            }
        }
    }

    It 'Omits decimalValue from the create body when -DecimalValue is not bound' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 999 } }

            New-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -MetaverseAttributeId 15 -ComparisonType Equals -StringValue 'x' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'POST' -and -not $Body.ContainsKey('decimalValue')
            }
        }
    }
}

Describe 'Set-JIMScopingCriterion -DecimalValue' {

    It 'Sends decimalValue as a decimal in the update body when -DecimalValue is bound' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 3 } }

            Set-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -CriterionId 3 -MetaverseAttributeId 15 -ComparisonType LessThan -DecimalValue 0.01d -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PUT' -and
                $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/scoping-criteria/5/criteria/3' -and
                $Body.decimalValue -is [decimal] -and
                $Body.decimalValue -eq 0.01d
            }
        }
    }

    It 'Omits decimalValue from the update body when -DecimalValue is not bound' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 3 } }

            Set-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -CriterionId 3 -MetaverseAttributeId 15 -ComparisonType Equals -StringValue 'x' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PUT' -and -not $Body.ContainsKey('decimalValue')
            }
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

Describe 'New-JIMScopingCriterion presence and pattern operators' {

    It 'Sends HasNoValue with no value when no value parameter is bound' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 999 } }

            New-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -MetaverseAttributeId 15 -ComparisonType HasNoValue -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'POST' -and
                $Body.comparisonType -eq 'HasNoValue' -and
                -not $Body.ContainsKey('stringValue') -and
                -not $Body.ContainsKey('intValue')
            }
        }
    }

    It 'Sends the pattern as the string value for MatchesPattern' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 999 } }

            New-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -ConnectedSystemAttributeId 40 -ComparisonType MatchesPattern -StringValue '^[a-z.]+@example\.org$' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'POST' -and
                $Body.comparisonType -eq 'MatchesPattern' -and
                $Body.stringValue -eq '^[a-z.]+@example\.org$'
            }
        }
    }

    It 'Sends HasValue on update' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 999 } }

            Set-JIMScopingCriterion -SyncRuleId 1 -GroupId 5 -CriterionId 3 -MetaverseAttributeId 15 -ComparisonType HasValue -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PUT' -and $Body.comparisonType -eq 'HasValue'
            }
        }
    }
}


Describe 'Test-JIMScope' {
    It 'Posts the sample values and as-of time to the scoping criteria test endpoint' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedEndpoint = $null
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'InScope' }
            }

            Test-JIMScope -SyncRuleId 12 -Attributes @{ department = 'Finance' } -AsOf '2027-01-31' | Out-Null

            $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/sync-rules/12/scoping-criteria/test'
            $script:capturedBody.attributes.department | Should -Be 'Finance'
            $script:capturedBody.asOf | Should -Be '2027-01-31T00:00:00'
            $script:capturedBody.ContainsKey('connectedSystemObjectId') | Should -BeFalse
        }
    }

    It 'Posts the object to load values from' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'OutOfScope' }
            }

            Test-JIMScope -SyncRuleId 12 -ConnectedSystemObjectId '0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01' | Out-Null

            $script:capturedBody.connectedSystemObjectId | Should -Be ([guid]'0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01')
            $script:capturedBody.ContainsKey('asOf') | Should -BeFalse
        }
    }

    It 'Takes the Synchronisation Rule from the pipeline' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedEndpoint = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                [PSCustomObject]@{ isValid = $true; outcome = 'InScope' }
            }

            [PSCustomObject]@{ Id = 15; Name = 'Finance App Users' } | Test-JIMScope -MetaverseObjectId '6f9619ff-8b86-d011-b42d-00c04fc964ff' | Out-Null

            $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/sync-rules/15/scoping-criteria/test'
        }
    }

    It 'Refuses a Connected System Object and a Metaverse Object together' {
        { Test-JIMScope -SyncRuleId 12 -ConnectedSystemObjectId ([guid]::NewGuid()) -MetaverseObjectId ([guid]::NewGuid()) } |
            Should -Throw -ErrorId 'AmbiguousParameterSet,Test-JIMScope'
    }

    It 'Returns the API object, so the explanation and next change reach the caller' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    IsValid     = $true
                    Outcome     = 'InScope'
                    Explanation = [PSCustomObject]@{ Criteria = @([PSCustomObject]@{ Met = $true }) }
                    NextChange  = [PSCustomObject]@{ At = '2026-12-01T00:00:00Z'; Outcome = 'OutOfScope' }
                }
            }

            $result = Test-JIMScope -SyncRuleId 12

            $result.Outcome | Should -Be 'InScope'
            $result.NextChange.Outcome | Should -Be 'OutOfScope'
            $result.Explanation.Criteria[0].Met | Should -BeTrue
        }
    }
}
