# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMConnectedSystemObjectUnresolvedReference (#959).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemObjectUnresolvedReference' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemObjectUnresolvedReference
        }

        It 'Should page by default' {
            $command.DefaultParameterSet | Should -Be 'Page'
        }

        It 'Should offer All and Summary parameter sets' {
            $command.ParameterSets.Name | Should -Contain 'All'
            $command.ParameterSets.Name | Should -Contain 'Summary'
        }
    }

    Context 'Requests' {

        It 'Should request one page of the object''s unresolved references, with the search encoded' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @([PSCustomObject]@{ attributeName = 'member'; value = 'CN=Ana Silva,OU=Contractors,DC=corp'; cause = 'ContainerNotInScope' }); hasNextPage = $false; totalCount = 1 } }

                $result = Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 3 -Id '6b1d55c3-0f0b-4f3e-9d1b-2b2b6f7e0959' -Page 2 -PageSize 10 -Search 'OU=Contractors'

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/3/connector-space/6b1d55c3-0f0b-4f3e-9d1b-2b2b6f7e0959/unresolved-references?page=2&pageSize=10&search=OU%3DContractors'
                }
                @($result).Count | Should -Be 1
                $result.cause | Should -Be 'ContainerNotInScope'
            }
        }

        It 'Should request the summary with -Summary' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ totalCount = 45; causes = @(); fixes = @() } }

                $result = Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 3 -Id '6b1d55c3-0f0b-4f3e-9d1b-2b2b6f7e0959' -Summary

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/3/connector-space/6b1d55c3-0f0b-4f3e-9d1b-2b2b6f7e0959/unresolved-references/summary'
                }
                $result.totalCount | Should -Be 45
            }
        }

        It 'Should take the object from the pipeline by its Connected System and id' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalCount = 0 } }

                [PSCustomObject]@{ ConnectedSystemId = 7; Id = [guid]'11111111-2222-3333-4444-555555555555' } |
                    Get-JIMConnectedSystemObjectUnresolvedReference | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/synchronisation/connected-systems/7/connector-space/11111111-2222-3333-4444-555555555555/unresolved-references?page=1&pageSize=50'
                }
            }
        }

        It 'Should page through every reference with -All' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:urPollCount = 0
                Mock Invoke-JIMApi {
                    $script:urPollCount++
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ value = "CN=Member $script:urPollCount" }); hasNextPage = ($script:urPollCount -lt 3); totalPages = 3; totalCount = 3 }
                }

                $result = Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 3 -Id '6b1d55c3-0f0b-4f3e-9d1b-2b2b6f7e0959' -All -PageSize 1

                Should -Invoke Invoke-JIMApi -Times 3 -Exactly
                @($result).Count | Should -Be 3
            }
        }
    }
}
