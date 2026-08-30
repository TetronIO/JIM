# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMConnectedSystem cmdlet.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystem' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystem
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have an ObjectTypes parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ObjectTypes'
        }

        It 'Should have a DeletionPreview parameter set' {
            $command.ParameterSets.Name | Should -Contain 'DeletionPreview'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystem
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $idParam = $command.Parameters['Id']
            $idParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Name parameter that supports wildcards' {
            $nameParam = $command.Parameters['Name']
            $nameParam.Attributes | Where-Object { $_ -is [System.Management.Automation.SupportsWildcardsAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have ObjectTypes as a switch parameter' {
            $command.Parameters['ObjectTypes'].SwitchParameter | Should -BeTrue
        }

        It 'Should have DeletionPreview as a switch parameter' {
            $command.Parameters['DeletionPreview'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMConnectedSystem -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when getting by ID without connection' {
            { Get-JIMConnectedSystem -Id 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMConnectedSystem -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have a description' {
            $help.Description | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the Id parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Id' } | Should -Not -BeNullOrEmpty
        }

        It 'Should document the Name parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Name' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }

        It 'Should document the ConfigurationDrift output shape' {
            # The -Id form carries drift status; callers script against these property names.
            ($help.returnValues | Out-String) | Should -Match 'ConfigurationDrift'
        }

        It 'Should warn that HasPendingChanges is false when drift cannot be determined' {
            # A caller gating a Full Synchronisation on this flag would otherwise silently skip systems that have
            # never been synchronised, or where change tracking is off.
            ($help.returnValues | Out-String) | Should -Match 'IsDeterminable'
        }
    }
}

Describe 'Remove-JIMConnectedSystem' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMConnectedSystem
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $idParam = $command.Parameters['Id']
            $idParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have InputObject parameter that accepts pipeline input' {
            $inputParam = $command.Parameters['InputObject']
            $inputParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a DeleteImmediately switch parameter' {
            $command.Parameters['DeleteImmediately'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Deletion mode (#809)' {

        It 'Sends the DELETE without a mode parameter by default (the server default is deprovisioning)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') { return }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Remove-JIMConnectedSystem -Id 1 -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -notmatch 'synchronisedDeprovisioning'
                }
            }
        }

        It 'Sends synchronisedDeprovisioning=false on the DELETE when -DeleteImmediately is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') { return }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Remove-JIMConnectedSystem -Id 1 -Force -DeleteImmediately

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -match 'synchronisedDeprovisioning=false'
                }
            }
        }

        It 'Combines synchronisedDeprovisioning=false and changeReason into one query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') { return }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Remove-JIMConnectedSystem -Id 1 -Force -DeleteImmediately -ChangeReason 'Decommissioned (CHG0123)'

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and
                    $Endpoint -match 'synchronisedDeprovisioning=false' -and
                    $Endpoint -match 'changeReason=' -and
                    @($Endpoint -split '\?').Count -eq 2
                }
            }
        }

        It 'Returns a tracking object with the Activity id when the deletion queues' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $activityId = [Guid]::NewGuid()
                $workerTaskId = [Guid]::NewGuid()
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') {
                        return [PSCustomObject]@{
                            Success      = $true
                            Outcome      = 'QueuedAsBackgroundJob'
                            ActivityId   = $activityId
                            WorkerTaskId = $workerTaskId
                        }
                    }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                $tracking = Remove-JIMConnectedSystem -Id 1 -Force

                $tracking | Should -Not -BeNullOrEmpty
                $tracking.ActivityId | Should -Be $activityId
                $tracking.WorkerTaskId | Should -Be $workerTaskId
                $tracking.Outcome | Should -Be 'QueuedAsBackgroundJob'
            }
        }

        It 'Consults the deletion preview before the confirmation' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') { return }
                    if ($Endpoint -match 'deletion-preview') {
                        return [PSCustomObject]@{
                            ConnectedSystemObjectCount  = 120
                            ContributedValueCount       = 300
                            ContributedValueObjectCount = 100
                            MvosWithDeletionRuleCount   = 5
                        }
                    }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Remove-JIMConnectedSystem -Id 1 -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'connected-systems/1/deletion-preview'
                }
            }
        }

        It 'Skips the preview lookup when -Force suppresses the confirmation' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') { return }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Remove-JIMConnectedSystem -Id 1 -Force

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter {
                    $Endpoint -match 'deletion-preview'
                }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should write error when not connected' {
            { Remove-JIMConnectedSystem -Id 1 -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMConnectedSystem -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should warn that -DeleteImmediately keeps contributed values unrecallably' {
            ($help.Parameters.Parameter | Where-Object { $_.Name -eq 'DeleteImmediately' } | Out-String) |
                Should -Match 'provenance'
        }

        It 'Should document the retry semantics for a failed deprovisioning run' {
            ($help.Description | Out-String) | Should -Match 'retry|retries|resumes'
        }

        It 'Should document that -DeleteImmediately on a fenced system finishes the deletion' {
            ($help.Description | Out-String) | Should -Match 'abandon'
        }

        It 'Should document the tracking output shape' {
            ($help.returnValues | Out-String) | Should -Match 'ActivityId'
        }
    }
}

Describe 'Get-JIMConnectedSystemDeletionImpactText' {

    It 'Returns nothing when there is no preview at all (the lookup failed)' {
        InModuleScope JIM {
            Get-JIMConnectedSystemDeletionImpactText -Preview $null | Should -BeNullOrEmpty
        }
    }

    It 'Describes the deprovisioning impact with counts' {
        InModuleScope JIM {
            $preview = [PSCustomObject]@{
                ConnectedSystemObjectCount  = 1200
                ContributedValueCount       = 3400
                ContributedValueObjectCount = 1100
                MvosWithDeletionRuleCount   = 7
            }
            $text = Get-JIMConnectedSystemDeletionImpactText -Preview $preview
            $text | Should -Match '1,200 Connected System Object'
            $text | Should -Match '3,400 contributed attribute value'
            $text | Should -Match '1,100 Metaverse Object'
            $text | Should -Match '7 Metaverse Object\(s\) will be evaluated for deletion'
        }
    }

    It 'Warns that immediate deletion keeps the values unrecallably' {
        InModuleScope JIM {
            $preview = [PSCustomObject]@{
                ConnectedSystemObjectCount  = 10
                ContributedValueCount       = 25
                ContributedValueObjectCount = 8
                MvosWithDeletionRuleCount   = 0
            }
            $text = Get-JIMConnectedSystemDeletionImpactText -Preview $preview -DeleteImmediately
            $text | Should -Match 'KEPT with no provenance'
            $text | Should -Match 'deleted immediately'
        }
    }
}
