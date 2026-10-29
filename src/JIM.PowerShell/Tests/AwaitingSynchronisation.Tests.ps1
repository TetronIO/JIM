# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for what the read cmdlets say about saved configuration changes awaiting synchronisation, and about
    Metaverse Objects awaiting export review, and for what the save cmdlets say about the Object Types a save made the next
    synchronisation process in full.
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

Describe 'Write-JIMAwaitingSynchronisationInformation' {

    BeforeAll {
        # Reads what the helper wrote to the information stream, which is recorded even while it is not displayed.
        function Get-InformationText {
            param($Entries)
            InModuleScope JIM -Parameters @{ Entries = $Entries } {
                param($Entries)
                @(Write-JIMAwaitingSynchronisationInformation -AwaitingSynchronisation $Entries 6>&1 |
                    Where-Object { $_ -is [System.Management.Automation.InformationRecord] } |
                    ForEach-Object { "$($_.MessageData)" })
            }
        }
    }

    It 'Writes nothing when the save recorded nothing' {
        Get-InformationText -Entries @() | Should -BeNullOrEmpty
    }

    It 'Writes nothing when the response carried no list at all' {
        Get-InformationText -Entries $null | Should -BeNullOrEmpty
    }

    It 'Names the system, the type, the object count and how long it takes at the last Full Synchronisation' {
        $entry = [PSCustomObject]@{
            ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 30000; EstimatedDuration = '00:15:00'
        }

        $lines = @(Get-InformationText -Entries @($entry))

        $lines | Should -HaveCount 1
        $lines[0] | Should -Match 'HR'
        $lines[0] | Should -Match 'every user object'
        $lines[0] | Should -Match '30,000 objects'
        $lines[0] | Should -Match 'about 15 minutes at the speed of its last Full Synchronisation'
    }

    It 'Writes one line per type' {
        $entries = @(
            [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 10; EstimatedDuration = $null }
            [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'group'; ObjectCount = 4; EstimatedDuration = $null }
        )

        Get-InformationText -Entries $entries | Should -HaveCount 2
    }

    It 'Leaves the duration out when the system has never completed a Full Synchronisation' {
        $entry = [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 12; EstimatedDuration = $null }

        $lines = @(Get-InformationText -Entries @($entry))

        $lines[0] | Should -Match '12 objects'
        $lines[0] | Should -Not -Match 'Full Synchronisation'
        $lines[0] | Should -Not -Match 'minute'
    }

    It 'Says under a minute for a short run' {
        $entry = [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 12; EstimatedDuration = '00:00:30' }

        (Get-InformationText -Entries @($entry))[0] | Should -Match 'under a minute at the speed'
    }

    It 'Says one minute, not one minutes, and one object, not one objects' {
        $entry = [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 1; EstimatedDuration = '00:01:10' }

        $line = (Get-InformationText -Entries @($entry))[0]

        $line | Should -Match '1 object,'
        $line | Should -Match 'about 1 minute at'
    }

    It 'Accepts a duration that is already a TimeSpan' {
        $entry = [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 500; EstimatedDuration = [TimeSpan]::FromMinutes(125) }

        (Get-InformationText -Entries @($entry))[0] | Should -Match 'about 125 minutes'
    }
}

Describe 'Cmdlets that say what a save made the next synchronisation process' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:Awaiting = @([PSCustomObject]@{
                    ConnectedSystemId = 1; ConnectedSystemName = 'HR'; ConnectedSystemObjectTypeId = 7
                    ObjectTypeName = 'user'; ObjectCount = 30000; EstimatedDuration = '00:15:00'
                })
        }
    }

    It 'New-JIMSyncRule writes a line for the type the new rule makes the system process' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 1; Name = 'Import Users'; AwaitingSynchronisation = $script:Awaiting } }

            New-JIMSyncRule -Name 'Import Users' -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 7 -MetaverseObjectTypeId 1 -Direction Import `
                -Confirm:$false -InformationVariable saved | Out-Null

            @($saved) | Should -HaveCount 1
            "$($saved[0].MessageData)" | Should -Match 'every user object'
        }
    }

    It 'Set-JIMSyncRule writes a line for each type, and returns the list on the saved rule' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 1; Name = 'Import Users'; AwaitingSynchronisation = $script:Awaiting } }

            $rule = Set-JIMSyncRule -Id 1 -Disable -Confirm:$false -PassThru -InformationVariable saved

            @($saved) | Should -HaveCount 1
            @($rule.AwaitingSynchronisation) | Should -HaveCount 1
            $rule.AwaitingSynchronisation[0].ObjectCount | Should -Be 30000
        }
    }

    It 'Set-JIMSyncRule writes nothing when the save recorded nothing' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 1; Name = 'Export Users'; AwaitingSynchronisation = @() } }

            Set-JIMSyncRule -Id 1 -Name 'Export Users' -Confirm:$false -InformationVariable saved | Out-Null

            @($saved) | Should -HaveCount 0
        }
    }

    It 'New-JIMSyncRuleMapping writes a line for the type the new Attribute Flow makes the system process' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 5; Warnings = @(); AwaitingSynchronisation = $script:Awaiting } }

            New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -Confirm:$false `
                -InformationVariable saved | Out-Null

            @($saved) | Should -HaveCount 1
            "$($saved[0].MessageData)" | Should -Match '30,000 objects'
        }
    }

    It 'Set-JIMSyncRuleMapping writes a line for the type the change makes the system process' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 5; Warnings = @(); DependentDerivedFlows = @(); AwaitingSynchronisation = $script:Awaiting } }

            Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Enabled $false -Confirm:$false -InformationVariable saved | Out-Null

            @($saved) | Should -HaveCount 1
        }
    }

    It 'Remove-JIMSyncRuleMapping writes a line for the type the deletion makes the system process' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedValueCount = 0; DependentDerivedFlows = @(); AwaitingSynchronisation = $script:Awaiting } }

            Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force -InformationVariable saved | Out-Null

            @($saved) | Should -HaveCount 1
        }
    }

    It 'New-JIMMatchingRule, Set-JIMMatchingRule and the Synchronisation Rule forms each write a line' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 7; AwaitingSynchronisation = $script:Awaiting } }

            New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 7 -MetaverseObjectTypeId 1 -TargetMetaverseAttributeId 5 `
                -SourceExpression 'Trim(cs["employeeId"])' -Confirm:$false -InformationVariable created | Out-Null
            Set-JIMMatchingRule -ConnectedSystemId 1 -Id 7 -CaseSensitive $true -Confirm:$false -InformationVariable changed | Out-Null
            New-JIMSyncRuleMatchingRule -SyncRuleId 3 -TargetMetaverseAttributeId 5 -SourceExpression 'Lower(cs["mail"])' -Confirm:$false `
                -InformationVariable ruleCreated | Out-Null
            Set-JIMSyncRuleMatchingRule -SyncRuleId 3 -Id 7 -SourceExpression 'Lower(cs["mail"])' -Confirm:$false `
                -InformationVariable ruleChanged | Out-Null

            @($created) | Should -HaveCount 1
            @($changed) | Should -HaveCount 1
            @($ruleCreated) | Should -HaveCount 1
            @($ruleChanged) | Should -HaveCount 1
        }
    }

    It 'Documents AwaitingSynchronisation where the caller looks for it' {
        foreach ($cmdlet in 'New-JIMSyncRule', 'Set-JIMSyncRule', 'New-JIMSyncRuleMapping', 'Set-JIMSyncRuleMapping',
            'Remove-JIMSyncRuleMapping', 'New-JIMMatchingRule', 'Set-JIMMatchingRule', 'New-JIMSyncRuleMatchingRule', 'Set-JIMSyncRuleMatchingRule') {
            ((Get-Help $cmdlet -Full).returnValues | Out-String) | Should -Match 'information stream' -Because "$cmdlet describes the line it writes"
        }
    }
}
