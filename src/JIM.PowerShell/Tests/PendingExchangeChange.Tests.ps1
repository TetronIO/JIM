# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Exchange queue cmdlets (#2021, Phase 6b).

.DESCRIPTION
    The behaviour these pin down that a reader would not guess from the parameters: a pipeline of queued changes is
    retried in ONE request, carrying every change's id and nothing else from the rows, so changes queued for different
    Connected Systems are all retried rather than only the last row's system's.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMPendingExchangeChange' {

    Context 'Parameter sets' {

        BeforeAll {
            $command = Get-Command Get-JIMPendingExchangeChange
        }

        It 'Should default to listing the queue, with ListAll and Summary sets' {
            $command.DefaultParameterSet | Should -Be 'List'
            $command.ParameterSets.Name | Should -Contain 'ListAll'
            $command.ParameterSets.Name | Should -Contain 'Summary'
        }

        It 'Should constrain State to the states a queued change can be in' {
            $validateSet = $command.Parameters['State'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('Queued', 'Held', 'Delivering', 'Retrying', 'Parked', 'Refused', 'NotMade')
        }

        It 'Should accept a Connected System by pipeline property name' {
            $command.Parameters['ConnectedSystemId'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } |
                Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requests' {

        It 'Should read the queue endpoint with the filters it was given' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ Items = @(); TotalCount = 0 } }
                $account = [guid]::NewGuid()

                Get-JIMPendingExchangeChange -ConnectedSystemId 3 -State Parked -FailureKind Refused -ConnectedSystemObjectId $account -Search 'Ada Lovelace' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/exchange/queue?*' -and
                    $Endpoint -like '*connectedSystemId=3*' -and
                    $Endpoint -like '*state=Parked*' -and
                    $Endpoint -like '*failureKind=Refused*' -and
                    $Endpoint -like "*connectedSystemObjectId=$account*" -and
                    $Endpoint -like '*search=Ada%20Lovelace*'
                }
            }
        }

        It 'Should return the rows from the response envelope' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Items      = @([PSCustomObject]@{ Id = [guid]::NewGuid(); MetaverseObjectDisplayName = 'Ada Lovelace'; State = 'Parked'; CanRetry = $true })
                        TotalCount = 1
                    }
                }

                $result = @(Get-JIMPendingExchangeChange)

                $result.Count | Should -Be 1
                $result[0].State | Should -Be 'Parked'
            }
        }

        It 'Should read the summary endpoint for -Summary' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ ParkedCount = 2; WaitingOnAPersonCount = 3 } }

                $summary = Get-JIMPendingExchangeChange -Summary

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq '/api/v1/exchange/queue/summary' }
                $summary.WaitingOnAPersonCount | Should -Be 3
            }
        }
    }
}

Describe 'Resume-JIMPendingExchangeChange' {

    Context 'Parameter validation' {

        BeforeAll {
            $command = Get-Command Resume-JIMPendingExchangeChange
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should accept queued changes from the pipeline by Id' {
            $param = $command.Parameters['Id']
            $param.ParameterType | Should -Be ([guid[]])
            $param.Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } |
                Should -Not -BeNullOrEmpty
        }

        It 'Should take only the ids from piped rows' {
            # A queued change's row carries its ConnectedSystemId too. Bound from the pipeline, the last row's value would be
            # sent alongside every id, and the server, combining the two, would retry only that system's changes.
            foreach ($name in @('ConnectedSystemId', 'MetaverseObjectId', 'ConnectedSystemObjectId', 'State', 'FailureKind')) {
                $command.Parameters[$name].Attributes |
                    Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and ($_.ValueFromPipelineByPropertyName -or $_.ValueFromPipeline) } |
                    Should -BeNullOrEmpty -Because "$name must not bind from a piped row"
            }
        }

        It 'Should offer only the states a retry can act on' {
            $validateSet = $command.Parameters['State'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('Retrying', 'Parked')
        }

        It 'Should require EntireQueue rather than borrowing -All from the Get cmdlet' {
            $command.Parameters['EntireQueue'] | Should -Not -BeNullOrEmpty
            $command.Parameters.ContainsKey('All') | Should -BeFalse
        }
    }

    Context 'Requests' {

        It 'Should retry a pipeline of changes from several Connected Systems in ONE request carrying every id' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 3 } }

                $rows = @(
                    [PSCustomObject]@{ Id = [guid]::NewGuid(); ConnectedSystemId = 3; State = 'Parked' }
                    [PSCustomObject]@{ Id = [guid]::NewGuid(); ConnectedSystemId = 4; State = 'Parked' }
                    [PSCustomObject]@{ Id = [guid]::NewGuid(); ConnectedSystemId = 4; State = 'Retrying' }
                )
                $rows | Resume-JIMPendingExchangeChange -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/exchange/queue/retry' -and $Method -eq 'POST' -and
                    $Body.ids.Count -eq 3 -and -not $Body.ContainsKey('connectedSystemId') -and -not $Body.ContainsKey('state')
                }
            }
        }

        It 'Should post the criteria it was given, search text included' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 2 } }

                Resume-JIMPendingExchangeChange -ConnectedSystemId 3 -State Parked -FailureKind InsufficientPermissions -Search 'Ada' -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.connectedSystemId -eq 3 -and $Body.state -eq 'Parked' -and
                    $Body.failureKind -eq 'InsufficientPermissions' -and $Body.searchText -eq 'Ada'
                }
            }
        }

        It 'Should refuse to act when nothing narrows the request' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 0 } }

                Resume-JIMPendingExchangeChange -Force -ErrorAction SilentlyContinue -ErrorVariable failure | Out-Null

                $failure | Should -Not -BeNullOrEmpty
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }

        It 'Should send applyToAllChanges when the whole queue is asked for explicitly' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 7 } }

                Resume-JIMPendingExchangeChange -EntireQueue -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Body.applyToAllChanges -eq $true }
            }
        }

        It 'Should do nothing quietly when an empty pipeline reaches it' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 0 } }

                @() | Resume-JIMPendingExchangeChange -Force -ErrorAction SilentlyContinue -ErrorVariable failure | Out-Null

                $failure | Should -BeNullOrEmpty
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }

        It 'Should not call the API under -WhatIf' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 1 } }

                Resume-JIMPendingExchangeChange -EntireQueue -WhatIf | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }
}
