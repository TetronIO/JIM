# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the step position carried on Worker Task and Schedule Execution output.

.DESCRIPTION
    The portal shows an administrator which step a run is on, and which step of a Schedule the run
    itself is (#1162). Automation reading the same objects could see the numbers, but had to compose
    the sentence itself, and every script that did so composed it slightly differently. Both cmdlets
    now carry the same sentence the portal and the live progress display use.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMWorkerTask step display' {

    It 'Names the step the run is on' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    id    = [guid]::NewGuid()
                    name  = 'Yellowstone APAC - Full Import'
                    steps = [PSCustomObject]@{
                        currentStepName   = 'Saving changes'
                        currentStepNumber = 3
                        totalSteps        = 7
                    }
                }
            }

            $task = Get-JIMWorkerTask -Id ([guid]::NewGuid())

            $task.StepDisplay | Should -Be 'Step 3 of 7: Saving changes'
        }
    }

    It 'Says nothing for a task that is not a Run Profile execution' {
        # Clearing Connected System Objects, example data generation and factory reset record no
        # steps. An empty "Step of" skeleton would be worse than an absent property.
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    id    = [guid]::NewGuid()
                    name  = 'Clear Connected System Objects'
                    steps = $null
                }
            }

            $task = Get-JIMWorkerTask -Id ([guid]::NewGuid())

            $task.StepDisplay | Should -BeNullOrEmpty
        }
    }

    It 'Carries the step display on every task in a listing' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    items = @(
                        [PSCustomObject]@{
                            id    = [guid]::NewGuid()
                            name  = 'Yellowstone APAC - Full Import'
                            steps = [PSCustomObject]@{ currentStepName = 'Importing objects'; currentStepNumber = 1; totalSteps = 7 }
                        },
                        [PSCustomObject]@{
                            id    = [guid]::NewGuid()
                            name  = 'Glitterband EMEA - Full Sync'
                            steps = [PSCustomObject]@{ currentStepName = 'Projecting'; currentStepNumber = 2; totalSteps = 4 }
                        }
                    )
                }
            }

            $tasks = @(Get-JIMWorkerTask)

            $tasks.StepDisplay | Should -Be @('Step 1 of 7: Importing objects', 'Step 2 of 4: Projecting')
        }
    }
}

Describe 'Get-JIMScheduleExecution step display' {

    It 'Names the step group the Schedule has reached, and what is running there' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    id       = [guid]::NewGuid()
                    progress = [PSCustomObject]@{
                        currentStepNumber = 2
                        totalSteps        = 5
                        steps             = @(
                            [PSCustomObject]@{ stepIndex = 0; name = 'Yellowstone APAC - Full Import'; status = 'Completed' },
                            [PSCustomObject]@{ stepIndex = 1; name = '2 in parallel'; status = 'Running' }
                        )
                    }
                }
            }

            $execution = Get-JIMScheduleExecution -Id ([guid]::NewGuid())

            $execution.StepDisplay | Should -Be 'Step 2 of 5: 2 in parallel'
        }
    }

    It 'Falls back to the recorded position where the response carries no step detail' {
        # The list and active reads return the summary shape, which has the counts but not the steps.
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                @([PSCustomObject]@{
                    id               = [guid]::NewGuid()
                    currentStepIndex = 1
                    totalSteps       = 5
                })
            }

            $executions = @(Get-JIMScheduleExecution -Active)

            $executions[0].StepDisplay | Should -Be 'Step 2 of 5'
        }
    }
}

Describe 'Schedule Execution step display from the cmdlets that start and stop one' {
    # Start-JIMSchedule and Stop-JIMScheduleExecution return the same Schedule Execution Get-JIMScheduleExecution -Id
    # does, so a script reading StepDisplay from one gets it from all three.

    BeforeAll {
        $script:Detail = {
            [PSCustomObject]@{
                id       = [guid]::NewGuid()
                status   = 'InProgress'
                progress = [PSCustomObject]@{
                    currentStepNumber = 1
                    totalSteps        = 3
                    steps             = @([PSCustomObject]@{ stepIndex = 0; name = 'HR - Delta Import'; status = 'Running' })
                }
            }
        }
    }

    It 'Start-JIMSchedule -PassThru names the step the new execution has reached' {
        InModuleScope JIM -Parameters @{ Detail = $script:Detail } {
            param($Detail)
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ executionId = [guid]::NewGuid() } } -ParameterFilter { $Endpoint -like '*/run' }
            Mock Invoke-JIMApi $Detail -ParameterFilter { $Endpoint -like '/api/v1/schedule-executions/*' }

            $execution = Start-JIMSchedule -Id ([guid]::NewGuid()) -PassThru

            $execution.StepDisplay | Should -Be 'Step 1 of 3: HR - Delta Import'
        }
    }

    It 'Stop-JIMScheduleExecution -PassThru names the step the cancelled execution had reached' {
        InModuleScope JIM -Parameters @{ Detail = $script:Detail } {
            param($Detail)
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = [guid]::NewGuid() } } -ParameterFilter { $Endpoint -like '*/cancel' }
            Mock Invoke-JIMApi $Detail -ParameterFilter { $Endpoint -notlike '*/cancel' }

            $execution = Stop-JIMScheduleExecution -Id ([guid]::NewGuid()) -Force -PassThru

            $execution.StepDisplay | Should -Be 'Step 1 of 3: HR - Delta Import'
        }
    }
}
