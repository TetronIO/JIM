# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for what the read cmdlets say about saved configuration changes awaiting synchronisation, and about
    Metaverse Objects awaiting export review.
.DESCRIPTION
    The five cmdlets return the API's object as it comes, so the new properties reach the caller without the module
    naming them. These tests pin that: a cmdlet that rebuilt its output from a list of properties would drop them. The
    help tests pin that the properties are described where the caller looks for them.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystem awaiting synchronisation' {

    It 'Returns the Object Types awaiting the next synchronisation, with their changes, on ConfigurationDrift' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    id                 = 3
                    name               = 'HR Import'
                    configurationDrift = [PSCustomObject]@{
                        hasPendingChanges     = $false
                        isDeterminable        = $true
                        changeCount           = 0
                        awaitingSynchronisation = @(
                            [PSCustomObject]@{
                                connectedSystemObjectTypeId = 7
                                objectTypeName              = 'user'
                                changeCount                 = 2
                                earliestSavedAt             = '2026-10-28T09:00:00Z'
                                objectCount                 = 12500
                                changes                     = @(
                                    [PSCustomObject]@{ reason = 'HR Inbound: Scope changed'; savedAt = '2026-10-28T09:00:00Z'; initiatedByName = 'Alice Adams' }
                                )
                            }
                        )
                    }
                }
            }

            $drift = (Get-JIMConnectedSystem -Id 3).configurationDrift

            $drift.awaitingSynchronisation | Should -HaveCount 1
            $drift.awaitingSynchronisation[0].objectTypeName | Should -Be 'user'
            $drift.awaitingSynchronisation[0].changeCount | Should -Be 2
            $drift.awaitingSynchronisation[0].objectCount | Should -Be 12500
            $drift.awaitingSynchronisation[0].changes[0].reason | Should -Be 'HR Inbound: Scope changed'
            $drift.hasPendingChanges | Should -BeFalse -Because 'the changes awaiting synchronisation are not drift'
        }
    }

    It 'Documents AwaitingSynchronisation, and that it is not counted as drift' {
        $help = Get-Help Get-JIMConnectedSystem -Full
        $outputs = $help.returnValues | Out-String

        $outputs | Should -Match 'AwaitingSynchronisation'
        $outputs | Should -Match 'do not set HasPendingChanges'
    }
}

Describe 'Get-JIMSyncRule awaiting synchronisation' {

    It 'Returns what each rule in a list is waiting on' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    items       = @(
                        [PSCustomObject]@{ id = 1; name = 'HR Inbound'; direction = 'Import'; awaitingSynchronisationChangeCount = 3; exportReviewPendingCount = 0 }
                        [PSCustomObject]@{ id = 2; name = 'AD Outbound'; direction = 'Export'; awaitingSynchronisationChangeCount = 0; exportReviewPendingCount = 1250 }
                    )
                    hasNextPage = $false
                    totalPages  = 1
                }
            }

            $rules = @(Get-JIMSyncRule)

            $rules | Should -HaveCount 2
            $rules[0].awaitingSynchronisationChangeCount | Should -Be 3
            $rules[1].exportReviewPendingCount | Should -Be 1250
        }
    }

    It 'Returns what a rule read by ID is waiting on' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{ id = 1; name = 'HR Inbound'; direction = 'Import'; awaitingSynchronisationChangeCount = 3; exportReviewPendingCount = 0 }
            }

            (Get-JIMSyncRule -Id 1).awaitingSynchronisationChangeCount | Should -Be 3
        }
    }

    It 'Documents both counts' {
        $outputs = (Get-Help Get-JIMSyncRule -Full).returnValues | Out-String

        $outputs | Should -Match 'AwaitingSynchronisationChangeCount'
        $outputs | Should -Match 'ExportReviewPendingCount'
    }
}

Describe 'Get-JIMActivity awaiting synchronisation' {

    It 'Returns the changes an Activity saved or processed in full, with whether they are applied' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    id                              = [guid]'11111111-1111-1111-1111-111111111111'
                    configurationChangeSynchronisations = @(
                        [PSCustomObject]@{
                            objectTypeName = 'user'
                            objectCount    = 12500
                            isApplied      = $true
                            appliedAt      = '2026-10-28T10:00:00Z'
                            changes        = @([PSCustomObject]@{ reason = 'HR Inbound: Scope changed' })
                        }
                    )
                }
            }

            $entry = (Get-JIMActivity -Id '11111111-1111-1111-1111-111111111111').configurationChangeSynchronisations

            $entry | Should -HaveCount 1
            $entry[0].isApplied | Should -BeTrue
            $entry[0].objectCount | Should -Be 12500
            $entry[0].changes[0].reason | Should -Be 'HR Inbound: Scope changed'
        }
    }

    It 'Documents ConfigurationChangeSynchronisations' {
        ((Get-Help Get-JIMActivity -Full).returnValues | Out-String) | Should -Match 'ConfigurationChangeSynchronisations'
    }
}

Describe 'Get-JIMMetaverseObjectType awaiting export review' {

    It 'Returns how many Metaverse Objects of the type are flagged for export review' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'User'; exportReviewPendingCount = 1250 } }

            (Get-JIMMetaverseObjectType -Id 1).exportReviewPendingCount | Should -Be 1250
        }
    }

    It 'Documents ExportReviewPendingCount' {
        ((Get-Help Get-JIMMetaverseObjectType -Full).returnValues | Out-String) | Should -Match 'ExportReviewPendingCount'
    }
}

Describe 'Get-JIMMetaverseObject awaiting export review' {

    It 'Returns whether the Metaverse Object is flagged for export review' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = [guid]'22222222-2222-2222-2222-222222222222'; exportReviewPending = $true } }

            (Get-JIMMetaverseObject -Id '22222222-2222-2222-2222-222222222222').exportReviewPending | Should -BeTrue
        }
    }

    It 'Documents ExportReviewPending' {
        ((Get-Help Get-JIMMetaverseObject -Full).returnValues | Out-String) | Should -Match 'ExportReviewPending'
    }
}
