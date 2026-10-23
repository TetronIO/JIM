# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Search-JIMMetaverseObjectChange.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Import-Module $ModulePath -Force
}

AfterAll {
    Remove-Module JIM -ErrorAction SilentlyContinue
}

Describe 'Search-JIMMetaverseObjectChange' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    items       = @([PSCustomObject]@{ id = 'a' }, [PSCustomObject]@{ id = 'b' })
                    hasNextPage = $false
                    totalPages  = 1
                }
            }
            Mock Resolve-JIMMetaverseAttribute { [PSCustomObject]@{ id = 5; name = $Name } }
            Mock Resolve-JIMMetaverseObjectType { [PSCustomObject]@{ id = 2; name = $Name } }
        }
    }

    It 'Posts to the search endpoint and returns the page items' {
        InModuleScope JIM {
            $result = @(Search-JIMMetaverseObjectChange)

            $result.Count | Should -Be 2
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/history/metaverse-object-changes/search' -and $Method -eq 'POST'
            }
        }
    }

    It 'Resolves -Attribute by name into a single condition' {
        InModuleScope JIM {
            Search-JIMMetaverseObjectChange -Attribute 'Employee Status' -Operator ChangedTo -Value 'Disabled' | Out-Null

            Should -Invoke Resolve-JIMMetaverseAttribute -Times 1 -Exactly -ParameterFilter { $Name -eq 'Employee Status' }
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Body.conditions.Count -eq 1 -and
                $Body.conditions[0].attributeId -eq 5 -and
                $Body.conditions[0].operator -eq 'ChangedTo' -and
                $Body.conditions[0].value -eq 'Disabled'
            }
        }
    }

    It 'Builds one condition per -Condition entry, by attribute name or id, with the match mode' {
        InModuleScope JIM {
            Search-JIMMetaverseObjectChange -MatchMode Any -Condition @(
                @{ Attribute = 'Employee Status'; Operator = 'ChangedTo'; Value = 'Disabled' },
                @{ AttributeId = 9; Operator = 'ChangedFrom' }
            ) | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Body.matchMode -eq 'Any' -and
                $Body.conditions.Count -eq 2 -and
                $Body.conditions[0].attributeId -eq 5 -and
                $Body.conditions[1].attributeId -eq 9 -and
                $Body.conditions[1].operator -eq 'ChangedFrom' -and
                $null -eq $Body.conditions[1].value
            }
        }
    }

    It 'Defaults a -Condition entry with no Operator to AnyChange' {
        InModuleScope JIM {
            Search-JIMMetaverseObjectChange -Condition @{ AttributeId = 9 } | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Body.conditions[0].operator -eq 'AnyChange' }
        }
    }

    It 'Refuses a -Condition entry naming no attribute' {
        InModuleScope JIM {
            { Search-JIMMetaverseObjectChange -Condition @{ Operator = 'ChangedTo'; Value = 'x' } -ErrorAction Stop } |
                Should -Throw '*Attribute*'
            Should -Invoke Invoke-JIMApi -Times 0
        }
    }

    It 'Resolves -ObjectTypeName and sends the display name and UTC time range' {
        InModuleScope JIM {
            $from = [DateTime]::new(2026, 10, 1, 0, 0, 0, [DateTimeKind]::Utc)
            $to = [DateTime]::new(2026, 10, 23, 0, 0, 0, [DateTimeKind]::Utc)

            Search-JIMMetaverseObjectChange -ObjectTypeName 'Person' -DisplayName 'Ana' -FromDate $from -ToDate $to | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Body.objectTypeId -eq 2 -and
                $Body.displayNameSearch -eq 'Ana' -and
                $Body.fromDate -eq '2026-10-01T00:00:00.0000000Z' -and
                $Body.toDate -eq '2026-10-23T00:00:00.0000000Z'
            }
        }
    }

    It 'Sends the page and page size' {
        InModuleScope JIM {
            Search-JIMMetaverseObjectChange -Page 3 -PageSize 10 | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Body.page -eq 3 -and $Body.pageSize -eq 10 }
        }
    }

    It 'Cannot combine -Attribute with -Condition' {
        InModuleScope JIM {
            { Search-JIMMetaverseObjectChange -Attribute 'Employee Status' -Condition @{ AttributeId = 9 } -ErrorAction Stop } |
                Should -Throw
        }
    }
}
