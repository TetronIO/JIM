# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Clear-JIMConnectedSystem cmdlet.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Clear-JIMConnectedSystem' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Clear-JIMConnectedSystem
        }

        It 'Should have ById as the default parameter set' {
            $command.DefaultParameterSet | Should -Be 'ById'
        }

        It 'Should have a ByInputObject parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ByInputObject'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Clear-JIMConnectedSystem
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a KeepChangeHistory switch parameter' {
            $command.Parameters['KeepChangeHistory'].SwitchParameter | Should -BeTrue
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $idParam = $command.Parameters['Id']
            $idParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have InputObject parameter that accepts pipeline input' {
            $inputParam = $command.Parameters['InputObject']
            $inputParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter as mandatory' {
            $idParam = $command.Parameters['Id']
            $idParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Wait switch parameter' {
            $command.Parameters['Wait'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a Timeout parameter constrained to positive seconds' {
            $param = $command.Parameters['Timeout']
            $param.ParameterType | Should -Be ([int])
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] -and $_.MinRange -eq 1 } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Queued clear behaviour (#1549)' {

        It 'Sends deleteChangeHistory=true on the POST by default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{
                            ActivityId = [guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
                            TaskId     = [guid]'11111111-2222-3333-4444-555555555555'
                            Message    = "Connector Space clear for 'HR System' has been queued."
                        }
                    }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Clear-JIMConnectedSystem -Id 1 -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'POST' -and $Endpoint -match 'deleteChangeHistory=true'
                }
            }
        }

        It 'Sends deleteChangeHistory=false on the POST when -KeepChangeHistory is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{
                            ActivityId = [guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
                            TaskId     = [guid]'11111111-2222-3333-4444-555555555555'
                            Message    = "Connector Space clear for 'HR System' has been queued."
                        }
                    }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Clear-JIMConnectedSystem -Id 1 -Force -KeepChangeHistory

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'POST' -and $Endpoint -match 'deleteChangeHistory=false'
                }
            }
        }

        It 'Emits the tracking object from the queued (202) response' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $activityId = [guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
                $taskId = [guid]'11111111-2222-3333-4444-555555555555'
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{
                            ActivityId = $activityId
                            TaskId     = $taskId
                            Message    = "Connector Space clear for 'HR System' has been queued."
                        }
                    }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                $result = Clear-JIMConnectedSystem -Id 1 -Force

                $result | Should -Not -BeNullOrEmpty
                $result.ActivityId | Should -Be $activityId
                $result.TaskId | Should -Be $taskId
                $result.Message | Should -Match 'HR System'
            }
        }

        It 'Waits for the clear to finish when -Wait is supplied, polling the returned ActivityId' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $activityId = [guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
                $script:progressCalls = 0
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ ActivityId = $activityId; TaskId = [guid]'11111111-2222-3333-4444-555555555555'; Message = 'queued' }
                    }
                    if ($Endpoint -match "activities/$activityId/progress") {
                        $script:progressCalls++
                        if ($script:progressCalls -lt 2) { return [PSCustomObject]@{ status = 'Running' } }
                        return [PSCustomObject]@{ status = 'Complete' }
                    }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Clear-JIMConnectedSystem -Id 1 -Force -Wait

                $script:progressCalls | Should -BeGreaterOrEqual 2
            }
        }

        It 'Does not poll the Activity when -Wait is not supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ ActivityId = [guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'; TaskId = [guid]'11111111-2222-3333-4444-555555555555'; Message = 'queued' }
                    }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                Clear-JIMConnectedSystem -Id 1 -Force

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Endpoint -match 'progress' }
            }
        }

        It 'Reports an error when the clear ends in failure under -Wait' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ ActivityId = [guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'; TaskId = [guid]'11111111-2222-3333-4444-555555555555'; Message = 'queued' }
                    }
                    if ($Endpoint -match 'activities/.+/progress') { return [PSCustomObject]@{ status = 'FailedWithError' } }
                    [PSCustomObject]@{ id = 1; name = 'HR System' }
                }

                { Clear-JIMConnectedSystem -Id 1 -Force -Wait -ErrorAction Stop } | Should -Throw '*FailedWithError*'
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should write error when not connected' {
            { Clear-JIMConnectedSystem -Id 1 -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Clear-JIMConnectedSystem -Full
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

        It 'Should document the KeepChangeHistory parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'KeepChangeHistory' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}
