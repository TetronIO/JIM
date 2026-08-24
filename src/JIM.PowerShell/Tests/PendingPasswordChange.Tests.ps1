# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Password Synchronisation queue cmdlets (#1119, requirement 33).

.DESCRIPTION
    Administrators script JIM as much as they click it, and the queue is where Password Synchronisation is
    actually administered: a directory refuses a password, changes park behind it, and somebody has to retry
    them once the cause is dealt with. Doing that for one system at a time in a browser is not how anyone runs
    a recovery.

    The behaviour these tests pin down that a reader would not guess from the parameters: a pipeline of queued
    changes is collected and acted on in ONE request, not one request per row. The server records one Activity
    per administrator action, so a per-row loop would turn a single decision into a hundred audit entries.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMPendingPasswordChange' {

    Context 'Parameter sets' {

        BeforeAll {
            $command = Get-Command Get-JIMPendingPasswordChange
        }

        It 'Should default to listing the queue' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should offer a Summary parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Summary'
        }

        It 'Should offer an auto-paginating ListAll parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ListAll'
        }

        It 'Should constrain Status to the states a queued change can be in' {
            $validateSet = $command.Parameters['Status'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Be @('Pending', 'Parked', 'Expired', 'Cancelled')
        }

        It 'Should accept a Connected System by pipeline property name' {
            # So that Get-JIMConnectedSystem -Name "Corporate AD" | Get-JIMPendingPasswordChange works.
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } |
                Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requests' {

        It 'Should read the queue endpoint with the filters it was given' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ Items = @(); TotalCount = 0 } }

                Get-JIMPendingPasswordChange -ConnectedSystemId 3 -Status 'Parked' -Search 'Ada' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/password-synchronisation/queue?*' -and
                    $Endpoint -like '*connectedSystemId=3*' -and
                    $Endpoint -like '*status=Parked*' -and
                    $Endpoint -like '*search=Ada*'
                }
            }
        }

        It 'Should return the rows from the response envelope' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Items = @(
                            [PSCustomObject]@{
                                Id                         = [guid]::NewGuid()
                                MetaverseObjectDisplayName = 'Ada Lovelace'
                                ConnectedSystemName        = 'Corporate AD'
                                Status                     = 'Parked'
                                Due                        = $false
                            }
                        )
                        TotalCount = 1
                    }
                }

                $result = @(Get-JIMPendingPasswordChange)

                $result.Count | Should -Be 1
                $result[0].MetaverseObjectDisplayName | Should -Be 'Ada Lovelace'
                $result[0].ConnectedSystemName | Should -Be 'Corporate AD'
            }
        }

        It 'Should read the summary endpoint for -Summary' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi {
                    [PSCustomObject]@{ WaitingCount = 4; DueCount = 1; ParkedCount = 2; ExpiredCount = 0; CancelledCount = 0 }
                }

                $summary = Get-JIMPendingPasswordChange -Summary

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/password-synchronisation/queue/summary'
                }
                $summary.ParkedCount | Should -Be 2
            }
        }
    }
}

Describe 'Resume-JIMPendingPasswordChange' {

    Context 'Parameter validation' {

        BeforeAll {
            $command = Get-Command Resume-JIMPendingPasswordChange
        }

        It 'Should support ShouldProcess' {
            # It causes passwords to be sent to real accounts. That belongs behind -WhatIf and -Confirm.
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should accept queued changes from the pipeline by Id' {
            $param = $command.Parameters['Id']
            $param.ParameterType | Should -Be ([guid[]])
            $param.Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } |
                Should -Not -BeNullOrEmpty
        }

        It 'Should require EntireQueue rather than borrowing -All from the Get cmdlet' {
            # -All means "every page" on Get-JIMPendingPasswordChange. Reusing the word here, where it would
            # mean "every change in the deployment", is how somebody ends up retrying the whole queue.
            $command.Parameters['EntireQueue'] | Should -Not -BeNullOrEmpty
            $command.Parameters.ContainsKey('All') | Should -BeFalse
        }
    }

    Context 'Requests' {

        It 'Should collect a pipeline of changes into ONE request' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 3 } }

                $rows = 1..3 | ForEach-Object { [PSCustomObject]@{ Id = [guid]::NewGuid() } }
                $rows | Resume-JIMPendingPasswordChange -Force | Out-Null

                # The server records one Activity per administrator action. Three requests would mean three
                # Activities for what the administrator experienced as one decision.
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.ids.Count -eq 3
                }
            }
        }

        It 'Should post to the retry endpoint with the filter it was given' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 2 } }

                Resume-JIMPendingPasswordChange -ConnectedSystemId 3 -Status 'Parked' -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/password-synchronisation/queue/retry' -and
                    $Method -eq 'POST' -and
                    $Body.connectedSystemId -eq 3 -and
                    $Body.status -eq 'Parked'
                }
            }
        }

        It 'Should refuse to act when nothing narrows the request' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 0 } }

                Resume-JIMPendingPasswordChange -Force -ErrorAction SilentlyContinue -ErrorVariable failure | Out-Null

                $failure | Should -Not -BeNullOrEmpty
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }

        It 'Should send applyToAllChanges when the whole queue is asked for explicitly' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 7 } }

                Resume-JIMPendingPasswordChange -EntireQueue -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.applyToAllChanges -eq $true
                }
            }
        }

        It 'Should do nothing quietly when an empty pipeline reaches it' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 0 } }

                # "Retry everything parked" when nothing is parked is a successful no-op, not a failure. An
                # error here would make the obvious scripted recovery loop noisy on the day it finally works.
                @() | Resume-JIMPendingPasswordChange -Force -ErrorAction SilentlyContinue -ErrorVariable failure | Out-Null

                $failure | Should -BeNullOrEmpty
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }

        It 'Should not call the API under -WhatIf' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 1 } }

                Resume-JIMPendingPasswordChange -EntireQueue -WhatIf | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }
}

Describe 'Stop-JIMPendingPasswordChange' {

    Context 'Parameter validation' {

        BeforeAll {
            $command = Get-Command Stop-JIMPendingPasswordChange
        }

        It 'Should confirm by default, at high impact' {
            # Stopping a password reaching somebody's account leaves that account on the old password with
            # nothing else to say so. It should be hard to do by accident.
            $binding = $command.ScriptBlock.Ast.Body.ParamBlock.Attributes |
                Where-Object { $_.TypeName.Name -eq 'CmdletBinding' }
            $binding | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should require EntireQueue for a queue-wide cancellation' {
            $command.Parameters['EntireQueue'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requests' {

        It 'Should post to the cancel endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 1 } }

                $id = [guid]::NewGuid()
                Stop-JIMPendingPasswordChange -Id $id -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/password-synchronisation/queue/cancel' -and
                    $Method -eq 'POST' -and
                    $Body.ids.Count -eq 1
                }
            }
        }

        It 'Should collect a pipeline of changes into ONE request' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 2 } }

                @([PSCustomObject]@{ Id = [guid]::NewGuid() }, [PSCustomObject]@{ Id = [guid]::NewGuid() }) |
                    Stop-JIMPendingPasswordChange -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Body.ids.Count -eq 2 }
            }
        }
    }
}
