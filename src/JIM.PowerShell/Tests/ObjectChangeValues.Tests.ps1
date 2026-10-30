# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMConnectedSystemObjectChangeValue and Get-JIMMetaverseObjectChangeValue: change history
    returns each attribute's first ten value changes and its total, and these page through the rest (#2498).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Import-Module $ModulePath -Force
}

AfterAll {
    Remove-Module JIM -ErrorAction SilentlyContinue
}

Describe '<Cmdlet>' -ForEach @(
    @{ Cmdlet = 'Get-JIMConnectedSystemObjectChangeValue'; Path = 'connected-system-object-changes' }
    @{ Cmdlet = 'Get-JIMMetaverseObjectChangeValue'; Path = 'metaverse-object-changes' }
) {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    items       = @([PSCustomObject]@{ valueChangeType = 'Add'; stringValue = 'a' }, [PSCustomObject]@{ valueChangeType = 'Add'; stringValue = 'b' })
                    hasNextPage = $false
                    totalPages  = 1
                    totalCount  = 2
                }
            }
        }
    }

    It 'Gets one page of the attribute''s value changes from the change''s values endpoint' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet; Path = $Path } {
            $changeId = [guid]'11111111-2222-3333-4444-555555555555'

            $result = @(& $Cmdlet -ChangeId $changeId -AttributeName 'Static Members' -Page 3 -PageSize 20)

            $result.Count | Should -Be 2
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq "/api/v1/history/$Path/$changeId/attributes/Static%20Members/values?page=3&pageSize=20"
            }
        }
    }

    It 'Passes -Search to the endpoint' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet } {
            & $Cmdlet -ChangeId ([guid]::NewGuid()) -AttributeName 'member' -Search 'Bob Jones' | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -like '*&search=Bob%20Jones' }
        }
    }

    It 'Fetches every page with -All' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet } {
            $result = @(& $Cmdlet -ChangeId ([guid]::NewGuid()) -AttributeName 'member' -All)

            $result.Count | Should -Be 2
            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -like '*page=1&pageSize=50' }
        }
    }

    It 'Writes an error and calls nothing when not connected' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet } {
            $script:JIMConnection = $null

            & $Cmdlet -ChangeId ([guid]::NewGuid()) -AttributeName 'member' -ErrorVariable failure -ErrorAction SilentlyContinue | Out-Null

            $failure | Should -Not -BeNullOrEmpty
            Should -Invoke Invoke-JIMApi -Times 0 -Exactly
        }
    }
}
