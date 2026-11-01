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

Describe 'Write-JIMAwaitingSynchronisationWarning' {

    BeforeAll {
        # Every warning the helper wrote, as an array of messages: an array even when there is one, so a test indexes a
        # message rather than the first character of a lone string.
        function Get-WarningMessages {
            param($Entries)
            InModuleScope JIM -Parameters @{ Entries = $Entries } {
                param($Entries)
                $messages = @(Write-JIMAwaitingSynchronisationWarning -AwaitingSynchronisation $Entries 3>&1 |
                        Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                        ForEach-Object { "$($_.Message)" })
                $messages
            }
        }
    }

    It 'Writes nothing when the save recorded nothing' {
        $messages = @(Get-WarningMessages -Entries @())

        $messages | Should -HaveCount 0
    }

    It 'Writes nothing when the response carried no list at all' {
        $messages = @(Get-WarningMessages -Entries $null)

        $messages | Should -HaveCount 0
    }

    It 'Names the system, the type, the object count and how long it takes at the last Full Synchronisation' {
        $entry = [PSCustomObject]@{
            ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 30000; EstimatedDuration = '00:15:00'
        }

        $messages = @(Get-WarningMessages -Entries @($entry))

        $messages | Should -HaveCount 1
        $messages[0] | Should -Match 'HR'
        $messages[0] | Should -Match 'every user object'
        $messages[0] | Should -Match '30,000 objects'
        $messages[0] | Should -Match 'about 15 minutes at the speed of its last Full Synchronisation'
    }

    It 'Writes one warning per type' {
        $entries = @(
            [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 10; EstimatedDuration = $null }
            [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'group'; ObjectCount = 4; EstimatedDuration = $null }
        )

        $messages = @(Get-WarningMessages -Entries $entries)

        $messages | Should -HaveCount 2
        $messages[0] | Should -Match 'every user object'
        $messages[1] | Should -Match 'every group object'
    }

    It 'Leaves the duration out when the system has never completed a Full Synchronisation' {
        $entry = [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 12; EstimatedDuration = $null }

        $messages = @(Get-WarningMessages -Entries @($entry))

        $messages | Should -HaveCount 1
        $messages[0] | Should -Match '12 objects'
        $messages[0] | Should -Not -Match 'Full Synchronisation'
        $messages[0] | Should -Not -Match 'minute'
    }

    It 'Says under a minute for a short run' {
        $entry = [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 12; EstimatedDuration = '00:00:30' }

        $messages = @(Get-WarningMessages -Entries @($entry))

        $messages | Should -HaveCount 1
        $messages[0] | Should -Match 'under a minute at the speed'
    }

    It 'Says one minute, not one minutes, and one object, not one objects' {
        $entry = [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 1; EstimatedDuration = '00:01:10' }

        $messages = @(Get-WarningMessages -Entries @($entry))

        $messages | Should -HaveCount 1
        $messages[0] | Should -Match '1 object,'
        $messages[0] | Should -Match 'about 1 minute at'
    }

    It 'Accepts a duration that is already a TimeSpan' {
        $entry = [PSCustomObject]@{ ConnectedSystemName = 'HR'; ObjectTypeName = 'user'; ObjectCount = 500; EstimatedDuration = [TimeSpan]::FromMinutes(125) }

        $messages = @(Get-WarningMessages -Entries @($entry))

        $messages | Should -HaveCount 1
        $messages[0] | Should -Match 'about 125 minutes'
    }
}

Describe 'Cmdlets that warn what a save made the next synchronisation process' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:Awaiting = @([PSCustomObject]@{
                    ConnectedSystemId = 1; ConnectedSystemName = 'HR'; ConnectedSystemObjectTypeId = 7
                    ObjectTypeName = 'user'; ObjectCount = 30000; EstimatedDuration = '00:15:00'
                })
        }
    }

    It 'New-JIMSyncRule warns for the type the new rule makes the system process' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 1; Name = 'Import Users'; AwaitingSynchronisation = $script:Awaiting } }

            New-JIMSyncRule -Name 'Import Users' -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 7 -MetaverseObjectTypeId 1 -Direction Import `
                -Confirm:$false -WarningVariable saved -WarningAction SilentlyContinue | Out-Null

            @($saved) | Should -HaveCount 1
            $saved[0].Message | Should -Match 'every user object'
        }
    }

    It 'Set-JIMSyncRule warns for each type, and returns the list on the saved rule' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 1; Name = 'Import Users'; AwaitingSynchronisation = $script:Awaiting } }

            $rule = Set-JIMSyncRule -Id 1 -Disable -Confirm:$false -PassThru -WarningVariable saved -WarningAction SilentlyContinue

            @($saved) | Should -HaveCount 1
            $saved[0].Message | Should -Match '30,000 objects'
            @($rule.AwaitingSynchronisation) | Should -HaveCount 1
            $rule.AwaitingSynchronisation[0].ObjectCount | Should -Be 30000
        }
    }

    It 'Set-JIMSyncRule warns of nothing when the save recorded nothing' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 1; Name = 'Export Users'; AwaitingSynchronisation = @() } }

            Set-JIMSyncRule -Id 1 -Name 'Export Users' -Confirm:$false -WarningVariable saved -WarningAction SilentlyContinue | Out-Null

            @($saved) | Should -HaveCount 0
        }
    }

    It 'New-JIMSyncRuleMapping warns for the type the new Attribute Flow makes the system process' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 5; Warnings = @(); AwaitingSynchronisation = $script:Awaiting } }

            New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -Confirm:$false `
                -WarningVariable saved -WarningAction SilentlyContinue | Out-Null

            @($saved) | Should -HaveCount 1
            $saved[0].Message | Should -Match '30,000 objects'
        }
    }

    It 'Set-JIMSyncRuleMapping warns for the type the change makes the system process' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 5; Warnings = @(); DependentDerivedFlows = @(); AwaitingSynchronisation = $script:Awaiting } }

            Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Enabled $false -Confirm:$false -WarningVariable saved -WarningAction SilentlyContinue | Out-Null

            @($saved) | Should -HaveCount 1
            $saved[0].Message | Should -Match 'every user object'
        }
    }

    It 'Remove-JIMSyncRuleMapping warns for the type the deletion makes the system process' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedValueCount = 0; DependentDerivedFlows = @(); AwaitingSynchronisation = $script:Awaiting } }

            Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force -WarningVariable saved -WarningAction SilentlyContinue | Out-Null

            @($saved) | Should -HaveCount 1
            $saved[0].Message | Should -Match 'every user object'
        }
    }

    It 'The Simple Mode and Advanced Mode matching rule cmdlets each warn for the type' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 7; AwaitingSynchronisation = $script:Awaiting } }

            New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 7 -MetaverseObjectTypeId 1 -TargetMetaverseAttributeId 5 `
                -SourceExpression 'Trim(cs["employeeId"])' -Confirm:$false -WarningVariable created -WarningAction SilentlyContinue | Out-Null
            Set-JIMMatchingRule -ConnectedSystemId 1 -Id 7 -CaseSensitive $true -Confirm:$false -WarningVariable changed -WarningAction SilentlyContinue | Out-Null
            New-JIMSyncRuleMatchingRule -SyncRuleId 3 -TargetMetaverseAttributeId 5 -SourceExpression 'Lower(cs["mail"])' -Confirm:$false `
                -WarningVariable ruleCreated -WarningAction SilentlyContinue | Out-Null
            Set-JIMSyncRuleMatchingRule -SyncRuleId 3 -Id 7 -SourceExpression 'Lower(cs["mail"])' -Confirm:$false `
                -WarningVariable ruleChanged -WarningAction SilentlyContinue | Out-Null

            @($created) | Should -HaveCount 1
            @($changed) | Should -HaveCount 1
            @($ruleCreated) | Should -HaveCount 1
            @($ruleChanged) | Should -HaveCount 1
            $created[0].Message | Should -Match 'every user object'
            $changed[0].Message | Should -Match 'every user object'
            $ruleCreated[0].Message | Should -Match 'every user object'
            $ruleChanged[0].Message | Should -Match 'every user object'
        }
    }

    It 'Set-JIMMetaverseAttributePriority warns for each type the reorder makes a system process, and returns the list with -PassThru' {
        InModuleScope JIM {
            $script:Awaiting += [PSCustomObject]@{
                ConnectedSystemId = 2; ConnectedSystemName = 'Directory'; ConnectedSystemObjectTypeId = 8
                ObjectTypeName = 'person'; ObjectCount = 4; EstimatedDuration = $null
            }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{ MetaverseObjectTypeId = 1; MetaverseAttributeId = 12; Contributors = @(); AwaitingSynchronisation = $script:Awaiting }
            }

            $order = Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 45, 12 -Confirm:$false -PassThru `
                -WarningVariable saved -WarningAction SilentlyContinue

            @($saved) | Should -HaveCount 2
            $saved[0].Message | Should -Match 'HR'
            $saved[0].Message | Should -Match 'every user object'
            $saved[1].Message | Should -Match 'Directory'
            $saved[1].Message | Should -Match 'every person object'
            @($order.AwaitingSynchronisation) | Should -HaveCount 2
        }
    }

    It 'Set-JIMMetaverseAttributePriority warns of nothing when the reorder recorded nothing' {
        InModuleScope JIM {
            Mock Invoke-JIMApi {
                [PSCustomObject]@{ MetaverseObjectTypeId = 1; MetaverseAttributeId = 12; Contributors = @(); AwaitingSynchronisation = @() }
            }

            Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 45, 12 -Confirm:$false `
                -WarningVariable saved -WarningAction SilentlyContinue | Out-Null

            @($saved) | Should -HaveCount 0
        }
    }

    It 'Move-JIMMetaverseAttributePriority warns for the type the move makes the system process, without -PassThru' {
        InModuleScope JIM {
            Mock Invoke-JIMApi {
                [PSCustomObject]@{ MetaverseObjectTypeId = 1; MetaverseAttributeId = 12; Contributors = @(); AwaitingSynchronisation = $script:Awaiting }
            }

            $returned = Move-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 78 -Position 1 -Confirm:$false `
                -WarningVariable saved -WarningAction SilentlyContinue

            $returned | Should -BeNullOrEmpty -Because 'without -PassThru the cmdlet returns nothing, and still warns'
            @($saved) | Should -HaveCount 1
            $saved[0].Message | Should -Match '30,000 objects'
        }
    }

    It 'Move-JIMMetaverseAttributePriority warns of nothing when the move recorded nothing' {
        InModuleScope JIM {
            Mock Invoke-JIMApi {
                [PSCustomObject]@{ MetaverseObjectTypeId = 1; MetaverseAttributeId = 12; Contributors = @(); AwaitingSynchronisation = @() }
            }

            Move-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 78 -Position 1 -Confirm:$false `
                -WarningVariable saved -WarningAction SilentlyContinue | Out-Null

            @($saved) | Should -HaveCount 0
        }
    }

    It 'Documents the warning where the caller looks for it' {
        foreach ($cmdlet in 'New-JIMSyncRule', 'Set-JIMSyncRule', 'New-JIMSyncRuleMapping', 'Set-JIMSyncRuleMapping',
            'Remove-JIMSyncRuleMapping', 'New-JIMMatchingRule', 'Set-JIMMatchingRule', 'New-JIMSyncRuleMatchingRule', 'Set-JIMSyncRuleMatchingRule',
            'Set-JIMMetaverseAttributePriority', 'Move-JIMMetaverseAttributePriority') {
            ((Get-Help $cmdlet -Full).returnValues | Out-String) | Should -Match 'warning stream' -Because "$cmdlet describes the warning it writes"
        }
    }
}
