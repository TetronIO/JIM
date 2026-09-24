# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Schedule cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMSchedule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMSchedule
        }

        It 'Should have an Id parameter' {
            $command.Parameters['Id'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have ScheduleId as an alias for Id' {
            $param = $command.Parameters['Id']
            $param.Aliases | Should -Contain 'ScheduleId'
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Name parameter with wildcard support' {
            $param = $command.Parameters['Name']
            $param | Should -Not -BeNullOrEmpty
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.SupportsWildcardsAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have an IncludeSteps switch parameter' {
            $command.Parameters['IncludeSteps'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Last-run outcome (#1196)' {

        It 'Passes the last execution fields through from the list endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                $executionId = [guid]::NewGuid()
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Items = @(
                            [PSCustomObject]@{
                                Id                            = [guid]::NewGuid()
                                Name                          = 'Nightly Sync'
                                LastExecutionId               = $executionId
                                LastExecutionStatus           = 'Failed'
                                LastExecutionCurrentStepIndex = 2
                                LastExecutionTotalSteps       = 6
                                LastExecutionErrorMessage     = 'Step 3 timed out'
                            }
                        )
                    }
                }

                $result = @(Get-JIMSchedule)

                $result.Count | Should -Be 1
                $result[0].LastExecutionId | Should -Be $executionId
                $result[0].LastExecutionStatus | Should -Be 'Failed'
                $result[0].LastExecutionCurrentStepIndex | Should -Be 2
                $result[0].LastExecutionTotalSteps | Should -Be 6
                $result[0].LastExecutionErrorMessage | Should -Be 'Step 3 timed out'
            }
        }

        It 'Preserves the last execution fields when -IncludeSteps re-fetches each Schedule' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                $scheduleId = [guid]::NewGuid()
                $executionId = [guid]::NewGuid()

                # The single-Schedule endpoint returns a ScheduleDetailDto built from the Schedule entity, which
                # carries no last-execution projection; only the list endpoint can supply those fields.
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Id    = $scheduleId
                        Name  = 'Nightly Sync'
                        Steps = @([PSCustomObject]@{ Id = [guid]::NewGuid(); StepIndex = 0 })
                    }
                } -ParameterFilter { $Endpoint -eq "/api/v1/schedules/$scheduleId" }

                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Items = @(
                            [PSCustomObject]@{
                                Id                            = $scheduleId
                                Name                          = 'Nightly Sync'
                                LastExecutionId               = $executionId
                                LastExecutionStatus           = 'Failed'
                                LastExecutionCurrentStepIndex = 2
                                LastExecutionTotalSteps       = 6
                                LastExecutionErrorMessage     = 'Step 3 timed out'
                            }
                        )
                    }
                } -ParameterFilter { $Endpoint -like '/api/v1/schedules?page=*' }

                $result = @(Get-JIMSchedule -IncludeSteps)

                $result.Count | Should -Be 1
                $result[0].Steps.Count | Should -Be 1
                $result[0].LastExecutionId | Should -Be $executionId
                $result[0].LastExecutionStatus | Should -Be 'Failed'
                $result[0].LastExecutionCurrentStepIndex | Should -Be 2
                $result[0].LastExecutionTotalSteps | Should -Be 6
                $result[0].LastExecutionErrorMessage | Should -Be 'Step 3 timed out'
            }
        }
    }

    Context 'Failure behaviour (#1787)' {

        It 'Passes OnStepFailure and the last run''s failed step indices through from the list endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Items = @(
                            [PSCustomObject]@{
                                Id                             = [guid]::NewGuid()
                                Name                           = 'Nightly HR sync'
                                OnStepFailure                  = 'Continue'
                                LastExecutionStatus            = 'CompleteWithError'
                                LastExecutionFailedStepIndices = @(2)
                            }
                        )
                    }
                }

                $result = @(Get-JIMSchedule)

                $result[0].OnStepFailure | Should -Be 'Continue'
                $result[0].LastExecutionStatus | Should -Be 'CompleteWithError'
                $result[0].LastExecutionFailedStepIndices | Should -Be @(2)
            }
        }

        It 'Preserves the failed step indices when -IncludeSteps re-fetches each Schedule' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $scheduleId = [guid]::NewGuid()

                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Id            = $scheduleId
                        Name          = 'Nightly HR sync'
                        OnStepFailure = 'Continue'
                        Steps         = @([PSCustomObject]@{ Id = [guid]::NewGuid(); StepIndex = 0; OnFailure = 'FollowSchedule'; ContinueOnFailure = $true; FailureBehaviourSource = 'Schedule' })
                    }
                } -ParameterFilter { $Endpoint -eq "/api/v1/schedules/$scheduleId" }

                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Items = @(
                            [PSCustomObject]@{
                                Id                             = $scheduleId
                                Name                           = 'Nightly HR sync'
                                LastExecutionStatus            = 'CompleteWithError'
                                LastExecutionFailedStepIndices = @(1, 3)
                            }
                        )
                    }
                } -ParameterFilter { $Endpoint -like '/api/v1/schedules?page=*' }

                $result = @(Get-JIMSchedule -IncludeSteps)

                $result[0].LastExecutionFailedStepIndices | Should -Be @(1, 3)
                $result[0].OnStepFailure | Should -Be 'Continue'
                $result[0].Steps[0].OnFailure | Should -Be 'FollowSchedule'
                $result[0].Steps[0].FailureBehaviourSource | Should -Be 'Schedule'
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMSchedule -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMSchedule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'New-JIMSchedule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMSchedule
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory TriggerType parameter' {
            $param = $command.Parameters['TriggerType']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have TriggerType parameter with ValidateSet' {
            $param = $command.Parameters['TriggerType']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Cron'
            $validateSet.ValidValues | Should -Contain 'Manual'
        }

        It 'Should have PatternType parameter with ValidateSet' {
            $param = $command.Parameters['PatternType']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'SpecificTimes'
            $validateSet.ValidValues | Should -Contain 'Interval'
            $validateSet.ValidValues | Should -Contain 'Custom'
        }

        It 'Should have DaysOfWeek parameter with validation' {
            $param = $command.Parameters['DaysOfWeek']
            $param | Should -Not -BeNullOrEmpty
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have IntervalUnit parameter with ValidateSet' {
            $param = $command.Parameters['IntervalUnit']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Hours'
            $validateSet.ValidValues | Should -Contain 'Minutes'
        }

        It 'Should have an Enabled switch parameter' {
            $command.Parameters['Enabled'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { New-JIMSchedule -Name "Test" -TriggerType Manual -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Failure behaviour (#1787)' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    [PSCustomObject]@{ id = [guid]::NewGuid(); name = $Body.name }
                }
            }
        }

        It 'Should have an OnStepFailure parameter with ValidateSet Stop and Continue' {
            $validateSet = (Get-Command New-JIMSchedule).Parameters['OnStepFailure'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('Stop', 'Continue')
        }

        It 'Sends -OnStepFailure by name' {
            InModuleScope JIM {
                New-JIMSchedule -Name 'Nightly HR sync' -TriggerType Manual -OnStepFailure Continue -Confirm:$false

                $script:capturedBody.onStepFailure | Should -BeExactly 'Continue'
            }
        }

        It 'Leaves onStepFailure out when not supplied, so the API applies its default of Stop' {
            InModuleScope JIM {
                New-JIMSchedule -Name 'Nightly HR sync' -TriggerType Manual -Confirm:$false

                $script:capturedBody.ContainsKey('onStepFailure') | Should -BeFalse
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMSchedule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Set-JIMSchedule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMSchedule
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMSchedule -Id ([guid]::NewGuid()) -Name "Test" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Failure behaviour (#1787)' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                $script:testScheduleId = [guid]::NewGuid()
                $script:existingStepId = [guid]::NewGuid()
                $script:testSchedule = [PSCustomObject]@{
                    id = $script:testScheduleId; name = 'Nightly HR sync'; description = $null
                    triggerType = 'Manual'; patternType = 'SpecificTimes'; isEnabled = $true
                    daysOfWeek = $null; runTimes = $null; intervalValue = $null; intervalUnit = $null
                    intervalWindowStart = $null; intervalWindowEnd = $null; cronExpression = $null
                    onStepFailure = 'Continue'
                    steps = @(
                        [PSCustomObject]@{
                            id = $script:existingStepId; stepIndex = 0; stepType = 'RunProfile'; executionMode = 'Sequential'
                            onFailure = 'FollowSchedule'; continueOnFailure = $true; failureBehaviourSource = 'Schedule'
                            timeoutSeconds = $null; connectedSystemId = 1; runProfileId = 1
                        }
                    )
                }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') { $script:capturedBody = $Body }
                    $script:testSchedule
                }
            }
        }

        It 'Should have an OnStepFailure parameter with ValidateSet Stop and Continue' {
            $validateSet = (Get-Command Set-JIMSchedule).Parameters['OnStepFailure'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('Stop', 'Continue')
        }

        It 'Sends -OnStepFailure by name' {
            InModuleScope JIM {
                Set-JIMSchedule -Id $script:testScheduleId -OnStepFailure Stop -Confirm:$false

                $script:capturedBody.onStepFailure | Should -BeExactly 'Stop'
            }
        }

        It 'Leaves onStepFailure out when not supplied, so the API keeps the stored setting' {
            InModuleScope JIM {
                Set-JIMSchedule -Id $script:testScheduleId -Description 'Nightly' -Confirm:$false

                $script:capturedBody.ContainsKey('onStepFailure') | Should -BeFalse
            }
        }

        It 'Round-trips the existing steps'' onFailure verbatim, without the effective continueOnFailure' {
            InModuleScope JIM {
                Set-JIMSchedule -Id $script:testScheduleId -OnStepFailure Stop -Confirm:$false

                $step = @($script:capturedBody.steps)[0]
                $step.id | Should -Be $script:existingStepId
                $step.onFailure | Should -BeExactly 'FollowSchedule'
                $step.ContainsKey('continueOnFailure') | Should -BeFalse
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMSchedule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Remove-JIMSchedule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMSchedule
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Remove-JIMSchedule -Id ([guid]::NewGuid()) -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMSchedule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Enable-JIMSchedule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Enable-JIMSchedule
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Enable-JIMSchedule -Id ([guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Enable-JIMSchedule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Disable-JIMSchedule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Disable-JIMSchedule
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Disable-JIMSchedule -Id ([guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Disable-JIMSchedule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Start-JIMSchedule' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Start-JIMSchedule
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Wait switch parameter' {
            $command.Parameters['Wait'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a Timeout parameter' {
            $command.Parameters['Timeout'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Start-JIMSchedule -Id ([guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Wait terminal-status detection' {

        # The API serialises ScheduleExecutionStatus by name (ApiJsonConfiguration), so the polled
        # 'status' is a string such as 'InProgress', never an ordinal. -Wait used to test
        # "$currentExecution.status -ge 2", which PowerShell evaluates as a *string* comparison
        # against '2' because the left operand types the comparison; every status name sorts above
        # '2', so the loop broke out on its very first poll and -Wait never waited.

        It 'Keeps polling while the execution is still InProgress' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $executionId = [guid]::NewGuid()
                $script:pollCount = 0

                Mock Start-Sleep { }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ executionId = $executionId }
                    }

                    $script:pollCount++
                    # Two in-flight polls, then a terminal one.
                    $status = if ($script:pollCount -ge 3) { 'Complete' } else { 'InProgress' }
                    [PSCustomObject]@{ id = $executionId; status = $status; currentStepIndex = 0; totalSteps = 2 }
                }

                $result = Start-JIMSchedule -Id ([guid]::NewGuid()) -Wait -Confirm:$false

                $script:pollCount | Should -BeGreaterThan 1
                $result.status | Should -Be 'Complete'
            }
        }

        It 'Stops on a Failed execution rather than waiting for the timeout' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $executionId = [guid]::NewGuid()

                Mock Start-Sleep { }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ executionId = $executionId }
                    }

                    [PSCustomObject]@{ id = $executionId; status = 'Failed'; currentStepIndex = 1; totalSteps = 2 }
                }

                $result = Start-JIMSchedule -Id ([guid]::NewGuid()) -Wait -Confirm:$false

                $result.status | Should -Be 'Failed'
                Should -Invoke Start-Sleep -Times 0 -Exactly
            }
        }

        It 'Stops on a CompleteWithError execution rather than waiting for the timeout (#1787)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $executionId = [guid]::NewGuid()

                Mock Start-Sleep { }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ executionId = $executionId }
                    }

                    [PSCustomObject]@{ id = $executionId; status = 'CompleteWithError'; currentStepIndex = 3; totalSteps = 4 }
                }

                # A short timeout bounds the failure mode: a -Wait that does not recognise the status spins until then.
                $result = Start-JIMSchedule -Id ([guid]::NewGuid()) -Wait -Timeout ([TimeSpan]::FromSeconds(2)) -Confirm:$false -WarningAction SilentlyContinue

                $result.status | Should -Be 'CompleteWithError'
                Should -Invoke Start-Sleep -Times 0 -Exactly
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Start-JIMSchedule -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Add-JIMScheduleStep' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Add-JIMScheduleStep
        }

        It 'Should have a mandatory ScheduleId parameter' {
            $param = $command.Parameters['ScheduleId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for ScheduleId' {
            $param = $command.Parameters['ScheduleId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory StepType parameter' {
            $param = $command.Parameters['StepType']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have StepType parameter with ValidateSet' {
            $param = $command.Parameters['StepType']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'RunProfile'
        }

        It 'Should have a Parallel switch parameter' {
            $command.Parameters['Parallel'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a ContinueOnFailure switch parameter' {
            $command.Parameters['ContinueOnFailure'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Add-JIMScheduleStep -ScheduleId ([guid]::NewGuid()) -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request body enum serialisation' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null

                $script:testScheduleId = [guid]::NewGuid()
                # As the API returns it: enum values serialised as names, not numbers (#1060).
                $existingStep = [PSCustomObject]@{
                    id                = [guid]::NewGuid()
                    stepIndex         = 0
                    stepType          = 'PowerShell'
                    executionMode     = 'Sequential'
                    continueOnFailure = $false
                    connectedSystemId = $null
                    runProfileId      = $null
                    name              = 'Nightly maintenance'
                    scriptPath        = '/scripts/nightly.ps1'
                    arguments         = $null
                    executablePath    = $null
                    workingDirectory  = $null
                }
                $script:testSchedule = [PSCustomObject]@{
                    id          = $script:testScheduleId
                    name        = 'Test Schedule'
                    description = $null
                    triggerType = 'Cron'
                    patternType = $null
                    isEnabled   = $true
                    daysOfWeek  = $null
                    runTimes    = $null
                    intervalValue = $null
                    intervalUnit = $null
                    intervalWindowStart = $null
                    intervalWindowEnd = $null
                    cronExpression = '0 1 * * *'
                    steps       = @($existingStep)
                }

                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') { $script:capturedBody = $Body }
                    $script:testSchedule
                }
            }
        }

        It 'Sends stepType and executionMode as enum names (the API rejects numeric enum values)' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -Confirm:$false

                $script:capturedBody | Should -Not -BeNullOrEmpty
                $newStep = $script:capturedBody.steps[-1]
                $newStep.stepType | Should -BeExactly 'RunProfile'
                $newStep.executionMode | Should -BeExactly 'Sequential'
            }
        }

        It 'Sends executionMode ParallelWithPrevious as an enum name when -Parallel is used' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -Parallel -Confirm:$false

                $script:capturedBody.steps[-1].executionMode | Should -BeExactly 'ParallelWithPrevious'
            }
        }

        It 'Preserves existing steps verbatim instead of coercing their enums to numeric defaults' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -Confirm:$false

                $script:capturedBody.steps.Count | Should -Be 2
                $script:capturedBody.steps[0].stepType | Should -BeExactly 'PowerShell'
                $script:capturedBody.steps[0].executionMode | Should -BeExactly 'Sequential'
            }
        }

        It 'Serialises a lone new step as a JSON array when the schedule had no steps' {
            InModuleScope JIM {
                # $existingSteps is assigned from an if-expression (the #1531 shape), which
                # collapses its output; the steps array is rebuilt through array concatenation
                # before serialisation, so the collapse is currently harmless. This pins that:
                # the first step added to an empty schedule must send steps as [ {...} ], not
                # a bare object.
                $script:testSchedule.steps = @()

                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -Confirm:$false

                $script:capturedBody.steps -is [System.Collections.ICollection] | Should -BeTrue
                (ConvertTo-Json -InputObject $script:capturedBody.steps -Depth 5 -Compress) | Should -Match '^\[\{'
                $script:capturedBody.steps.Count | Should -Be 1
            }
        }
    }

    Context 'Failure behaviour (#1787)' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                $script:testScheduleId = [guid]::NewGuid()
                $script:existingStepId = [guid]::NewGuid()

                # A Schedule set to continue, whose one step follows it: the API reports that step's EFFECTIVE
                # behaviour as continueOnFailure = true. Echoing that back is what used to pin the step to Continue.
                $existingStep = [PSCustomObject]@{
                    id                     = $script:existingStepId
                    stepIndex              = 0
                    stepType               = 'RunProfile'
                    executionMode          = 'Sequential'
                    onFailure              = 'FollowSchedule'
                    continueOnFailure      = $true
                    failureBehaviourSource = 'Schedule'
                    timeoutSeconds         = 600
                    connectedSystemId      = 1
                    runProfileId           = 1
                    name                   = $null
                    scriptPath             = $null
                    arguments              = $null
                    executablePath         = $null
                    workingDirectory       = $null
                    sqlConnectionString    = $null
                    sqlScriptPath          = $null
                }
                $script:testSchedule = [PSCustomObject]@{
                    id = $script:testScheduleId; name = 'Nightly HR sync'; description = $null
                    triggerType = 'Manual'; patternType = 'SpecificTimes'; isEnabled = $true
                    daysOfWeek = $null; runTimes = $null; intervalValue = $null; intervalUnit = $null
                    intervalWindowStart = $null; intervalWindowEnd = $null; cronExpression = $null
                    onStepFailure = 'Continue'
                    steps = @($existingStep)
                }

                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') { $script:capturedBody = $Body }
                    $script:testSchedule
                }
            }
        }

        It 'Should have an OnFailure parameter with ValidateSet FollowSchedule, Stop and Continue' {
            $validateSet = (Get-Command Add-JIMScheduleStep).Parameters['OnFailure'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('FollowSchedule', 'Stop', 'Continue')
        }

        It 'Adds a step that follows the Schedule by default, sending onFailure rather than continueOnFailure' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -Confirm:$false

                $newStep = $script:capturedBody.steps[-1]
                $newStep.onFailure | Should -BeExactly 'FollowSchedule'
                $newStep.ContainsKey('continueOnFailure') | Should -BeFalse
            }
        }

        It 'Treats -ContinueOnFailure as shorthand for -OnFailure Continue' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -ContinueOnFailure -Confirm:$false

                $script:capturedBody.steps[-1].onFailure | Should -BeExactly 'Continue'
            }
        }

        It 'Sends -OnFailure as the new step''s setting' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -OnFailure Stop -Confirm:$false

                $script:capturedBody.steps[-1].onFailure | Should -BeExactly 'Stop'
            }
        }

        It 'Refuses -OnFailure and -ContinueOnFailure together with a terminating error, and sends nothing' {
            InModuleScope JIM {
                { Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -OnFailure Stop -ContinueOnFailure -Confirm:$false } |
                    Should -Throw '*OnFailure*ContinueOnFailure*'
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'PUT' }
            }
        }

        It 'Round-trips each existing step''s onFailure verbatim, so a step following a continuing Schedule is not pinned' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -Confirm:$false

                $existing = $script:capturedBody.steps[0]
                $existing.id | Should -Be $script:existingStepId
                $existing.onFailure | Should -BeExactly 'FollowSchedule'
                $existing.ContainsKey('continueOnFailure') | Should -BeFalse
                $existing.ContainsKey('failureBehaviourSource') | Should -BeFalse
            }
        }

        It 'Preserves an existing step''s timeout, which it used to drop' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -Confirm:$false

                $script:capturedBody.steps[0].timeoutSeconds | Should -Be 600
            }
        }

        It 'Leaves the Schedule''s own onStepFailure out of the update, so it stays unchanged' {
            InModuleScope JIM {
                Add-JIMScheduleStep -ScheduleId $script:testScheduleId -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 2 -Confirm:$false

                $script:capturedBody.ContainsKey('onStepFailure') | Should -BeFalse
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Add-JIMScheduleStep -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Set-JIMScheduleStep' {

    BeforeAll {
        $command = Get-Command Set-JIMScheduleStep -ErrorAction SilentlyContinue
    }

    Context 'Parameter Validation' {

        It 'Should be exported from the module' {
            (Get-Module JIM).ExportedFunctions.Keys | Should -Contain 'Set-JIMScheduleStep'
        }

        It 'Should have a mandatory ScheduleId parameter that accepts pipeline input by property name' {
            $param = $command.Parameters['ScheduleId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should alias ScheduleId as Id, so a piped Schedule binds' {
            $command.Parameters['ScheduleId'].Aliases | Should -Contain 'Id'
        }

        It 'Should have a mandatory StepId parameter' {
            $param = $command.Parameters['StepId']
            $param.ParameterType | Should -Be ([guid])
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory OnFailure parameter with ValidateSet FollowSchedule, Stop and Continue' {
            $param = $command.Parameters['OnFailure']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
            ($param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }).ValidValues | Should -Be @('FollowSchedule', 'Stop', 'Continue')
        }

        It 'Should have ChangeReason and PassThru parameters' {
            $command.Parameters['ChangeReason'] | Should -Not -BeNullOrEmpty
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMScheduleStep -ScheduleId ([guid]::NewGuid()) -StepId ([guid]::NewGuid()) -OnFailure Stop -Confirm:$false -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'The request it builds' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                $script:testScheduleId = [guid]::NewGuid()
                $script:targetStepId = [guid]::NewGuid()
                $script:otherStepId = [guid]::NewGuid()

                $newStep = {
                    param($id, $index, $onFailure, $continueOnFailure, $source)
                    [PSCustomObject]@{
                        id = $id; stepIndex = $index; stepType = 'RunProfile'; executionMode = 'Sequential'
                        onFailure = $onFailure; continueOnFailure = $continueOnFailure; failureBehaviourSource = $source
                        timeoutSeconds = 900; connectedSystemId = 1; runProfileId = $index + 1
                        name = $null; scriptPath = $null; arguments = $null; executablePath = $null
                        workingDirectory = $null; sqlConnectionString = $null; sqlScriptPath = $null
                    }
                }
                $script:testSchedule = [PSCustomObject]@{
                    id = $script:testScheduleId; name = 'Nightly HR sync'; description = 'Nightly'
                    triggerType = 'Cron'; patternType = 'SpecificTimes'; isEnabled = $true
                    daysOfWeek = '1,2,3,4,5'; runTimes = '02:00'; intervalValue = $null; intervalUnit = $null
                    intervalWindowStart = $null; intervalWindowEnd = $null; cronExpression = '0 2 * * 1-5'
                    onStepFailure = 'Continue'
                    steps = @(
                        (& $newStep $script:otherStepId 0 'FollowSchedule' $true 'Schedule'),
                        (& $newStep $script:targetStepId 1 'FollowSchedule' $true 'Schedule')
                    )
                }

                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') { $script:capturedBody = $Body }
                    $script:testSchedule
                }
            }
        }

        It 'Changes only the target step''s onFailure and round-trips every other step verbatim' {
            InModuleScope JIM {
                Set-JIMScheduleStep -ScheduleId $script:testScheduleId -StepId $script:targetStepId -OnFailure Stop -Confirm:$false

                $steps = @($script:capturedBody.steps)
                $steps.Count | Should -Be 2
                $target = $steps | Where-Object { $_.id -eq $script:targetStepId }
                $other = $steps | Where-Object { $_.id -eq $script:otherStepId }
                $target.onFailure | Should -BeExactly 'Stop'
                $other.onFailure | Should -BeExactly 'FollowSchedule'
                $steps | ForEach-Object { $_.ContainsKey('continueOnFailure') | Should -BeFalse }
                $target.timeoutSeconds | Should -Be 900
                $target.stepIndex | Should -Be 1
            }
        }

        It 'Keeps the Schedule''s own settings and leaves onStepFailure out' {
            InModuleScope JIM {
                Set-JIMScheduleStep -ScheduleId $script:testScheduleId -StepId $script:targetStepId -OnFailure Continue -Confirm:$false

                $script:capturedBody.name | Should -Be 'Nightly HR sync'
                $script:capturedBody.triggerType | Should -Be 'Cron'
                $script:capturedBody.cronExpression | Should -Be '0 2 * * 1-5'
                $script:capturedBody.isEnabled | Should -BeTrue
                $script:capturedBody.ContainsKey('onStepFailure') | Should -BeFalse
            }
        }

        It 'Sends -ChangeReason with the update' {
            InModuleScope JIM {
                Set-JIMScheduleStep -ScheduleId $script:testScheduleId -StepId $script:targetStepId -OnFailure Stop -ChangeReason 'Export must not run on a failed import (CHG0101)' -Confirm:$false

                $script:capturedBody.changeReason | Should -Be 'Export must not run on a failed import (CHG0101)'
            }
        }

        It 'Binds a piped Schedule''s Id to ScheduleId' {
            InModuleScope JIM {
                [PSCustomObject]@{ Id = $script:testScheduleId; Name = 'Nightly HR sync' } |
                    Set-JIMScheduleStep -StepId $script:targetStepId -OnFailure Stop -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Method -eq 'PUT' -and $Endpoint -eq "/api/v1/schedules/$($script:testScheduleId)" }
            }
        }

        It 'Returns the updated Schedule with -PassThru' {
            InModuleScope JIM {
                $result = Set-JIMScheduleStep -ScheduleId $script:testScheduleId -StepId $script:targetStepId -OnFailure Stop -PassThru -Confirm:$false

                $result.id | Should -Be $script:testScheduleId
            }
        }

        It 'Returns nothing without -PassThru' {
            InModuleScope JIM {
                $result = Set-JIMScheduleStep -ScheduleId $script:testScheduleId -StepId $script:targetStepId -OnFailure Stop -Confirm:$false

                $result | Should -BeNullOrEmpty
            }
        }

        It 'Reports a step that is not in the Schedule, and sends nothing' {
            InModuleScope JIM {
                $missingStepId = [guid]::NewGuid()

                { Set-JIMScheduleStep -ScheduleId $script:testScheduleId -StepId $missingStepId -OnFailure Stop -Confirm:$false -ErrorAction Stop } |
                    Should -Throw "*$missingStepId*not found*"
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'PUT' }
            }
        }

        It 'Sends nothing under -WhatIf' {
            InModuleScope JIM {
                Set-JIMScheduleStep -ScheduleId $script:testScheduleId -StepId $script:targetStepId -OnFailure Stop -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'PUT' }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMScheduleStep -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'ConvertTo-JIMScheduleStepRequest (private)' {

    It 'Round-trips onFailure verbatim and never sends the effective continueOnFailure' {
        InModuleScope JIM {
            $step = [PSCustomObject]@{
                Id = [guid]::NewGuid(); StepIndex = 2; StepType = 'RunProfile'; ExecutionMode = 'ParallelWithPrevious'
                OnFailure = 'FollowSchedule'; ContinueOnFailure = $true; FailureBehaviourSource = 'Schedule'
                TimeoutSeconds = 120; ConnectedSystemId = 3; RunProfileId = 7; Name = $null
            }

            $request = ConvertTo-JIMScheduleStepRequest -Step $step

            $request.onFailure | Should -BeExactly 'FollowSchedule'
            $request.ContainsKey('continueOnFailure') | Should -BeFalse
            $request.ContainsKey('failureBehaviourSource') | Should -BeFalse
        }
    }

    It 'Carries every field of a step request, including the id that identifies an existing step' {
        InModuleScope JIM {
            $id = [guid]::NewGuid()
            $step = [PSCustomObject]@{
                id = $id; stepIndex = 1; stepType = 'SqlScript'; executionMode = 'Sequential'; onFailure = 'Stop'
                timeoutSeconds = 60; connectedSystemId = $null; runProfileId = $null; name = 'Tidy staging'
                scriptPath = $null; arguments = $null; executablePath = $null; workingDirectory = $null
                sqlConnectionString = 'Server=db;Database=staging'; sqlScriptPath = '/scripts/tidy.sql'
            }

            $request = ConvertTo-JIMScheduleStepRequest -Step $step

            $request.id | Should -Be $id
            $request.stepIndex | Should -Be 1
            $request.stepType | Should -BeExactly 'SqlScript'
            $request.executionMode | Should -BeExactly 'Sequential'
            $request.onFailure | Should -BeExactly 'Stop'
            $request.timeoutSeconds | Should -Be 60
            $request.name | Should -Be 'Tidy staging'
            $request.sqlConnectionString | Should -Be 'Server=db;Database=staging'
            $request.sqlScriptPath | Should -Be '/scripts/tidy.sql'
        }
    }

    It 'Applies a -StepIndex or -OnFailure override' {
        InModuleScope JIM {
            $step = [PSCustomObject]@{ id = [guid]::NewGuid(); stepIndex = 4; stepType = 'RunProfile'; executionMode = 'Sequential'; onFailure = 'FollowSchedule'; connectedSystemId = 1; runProfileId = 1 }

            $request = ConvertTo-JIMScheduleStepRequest -Step $step -StepIndex 2 -OnFailure Continue

            $request.stepIndex | Should -Be 2
            $request.onFailure | Should -BeExactly 'Continue'
        }
    }
}

Describe 'Remove-JIMScheduleStep' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMScheduleStep
        }

        It 'Should have a mandatory ScheduleId parameter' {
            $param = $command.Parameters['ScheduleId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory StepIndex parameter' {
            $param = $command.Parameters['StepIndex']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have StepIndex parameter with validation' {
            $param = $command.Parameters['StepIndex']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Remove-JIMScheduleStep -ScheduleId ([guid]::NewGuid()) -StepIndex 0 -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'The request it builds' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                $script:testScheduleId = [guid]::NewGuid()

                $step = {
                    param($index)
                    [PSCustomObject]@{
                        id = [guid]::NewGuid(); stepIndex = $index; stepType = 'RunProfile'
                        executionMode = 'Sequential'; continueOnFailure = $false
                        connectedSystemId = 1; runProfileId = $index + 1
                        name = $null; scriptPath = $null; arguments = $null
                        executablePath = $null; workingDirectory = $null
                    }
                }
                $script:testSchedule = [PSCustomObject]@{
                    id = $script:testScheduleId; name = 'Test Schedule'; description = $null
                    triggerType = 'Cron'; patternType = $null; isEnabled = $true
                    daysOfWeek = $null; runTimes = $null; intervalValue = $null
                    intervalUnit = $null; intervalWindowStart = $null; intervalWindowEnd = $null
                    cronExpression = '0 1 * * *'
                    steps = @((& $step 0), (& $step 1))
                }

                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') { $script:capturedBody = $Body }
                    $script:testSchedule
                }
            }
        }

        It 'Keeps a single remaining step a JSON array at the serialisation boundary' {
            InModuleScope JIM {
                # $existingSteps is assigned from an if-expression (the #1531 shape), which
                # collapses its output; the remaining steps are rebuilt through array
                # concatenation before serialisation, so the collapse is currently harmless.
                # This pins that: a schedule reduced to one step must send [ {...} ], not a
                # bare object.
                Remove-JIMScheduleStep -ScheduleId $script:testScheduleId -StepIndex 1 -Force

                $script:capturedBody.steps -is [System.Collections.ICollection] | Should -BeTrue
                (ConvertTo-Json -InputObject $script:capturedBody.steps -Depth 5 -Compress) | Should -Match '^\[\{'
                $script:capturedBody.steps.Count | Should -Be 1
            }
        }

        It 'Keeps each remaining step''s id and onFailure, and sends no continueOnFailure (#1787)' {
            InModuleScope JIM {
                # Sending remaining steps without their ids deleted and recreated them, and resending the effective
                # continueOnFailure as a NEW step pinned any step that followed a continuing Schedule to Continue.
                $keptStep = $script:testSchedule.steps[1]
                $keptStep | Add-Member -NotePropertyName onFailure -NotePropertyValue 'FollowSchedule'
                $keptStep.continueOnFailure = $true
                $keptStep | Add-Member -NotePropertyName timeoutSeconds -NotePropertyValue 300

                Remove-JIMScheduleStep -ScheduleId $script:testScheduleId -StepIndex 0 -Force

                $sent = $script:capturedBody.steps[0]
                $sent.id | Should -Be $keptStep.id
                $sent.stepIndex | Should -Be 0
                $sent.onFailure | Should -BeExactly 'FollowSchedule'
                $sent.timeoutSeconds | Should -Be 300
                $sent.ContainsKey('continueOnFailure') | Should -BeFalse
            }
        }

        It 'Sends an empty steps array, not null, when the last step is removed' {
            InModuleScope JIM {
                $script:testSchedule.steps = @($script:testSchedule.steps[0])

                Remove-JIMScheduleStep -ScheduleId $script:testScheduleId -StepIndex 0 -Force

                $script:capturedBody.ContainsKey('steps') | Should -BeTrue
                (ConvertTo-Json -InputObject $script:capturedBody.steps -Compress) | Should -Be '[]'
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMScheduleStep -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Get-JIMScheduleExecution' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMScheduleExecution
        }

        It 'Should have an Id parameter' {
            $command.Parameters['Id'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have ExecutionId as an alias for Id' {
            $param = $command.Parameters['Id']
            $param.Aliases | Should -Contain 'ExecutionId'
        }

        It 'Should have a ScheduleId parameter for filtering' {
            $command.Parameters['ScheduleId'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Status parameter with ValidateSet' {
            $param = $command.Parameters['Status']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Queued'
            $validateSet.ValidValues | Should -Contain 'InProgress'
            $validateSet.ValidValues | Should -Contain 'Complete'
            $validateSet.ValidValues | Should -Contain 'Failed'
            $validateSet.ValidValues | Should -Contain 'Cancelled'
        }

        It 'Should accept CompleteWithError as a Status (#1787)' {
            $param = $command.Parameters['Status']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Contain 'CompleteWithError'
        }

        It 'Should no longer accept the pre-#1196 "Completed" spelling, which the API now rejects' {
            $param = $command.Parameters['Status']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Not -Contain 'Completed'
        }

        It 'Should have an Active switch parameter' {
            $command.Parameters['Active'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Pipeline binding' {

        It 'Filters active executions to the piped Schedule (as documented: Get-JIMSchedule | Get-JIMScheduleExecution -Active)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                $targetScheduleId = [guid]::NewGuid()
                $otherScheduleId = [guid]::NewGuid()
                $matching = [PSCustomObject]@{ id = [guid]::NewGuid(); scheduleId = $targetScheduleId; scheduleName = 'Weekday Sync'; status = 'InProgress' }
                $other = [PSCustomObject]@{ id = [guid]::NewGuid(); scheduleId = $otherScheduleId; scheduleName = 'Other Schedule'; status = 'Queued' }
                Mock Invoke-JIMApi { @($matching, $other) }

                # As returned by Get-JIMSchedule: a PSCustomObject exposing Id (not ScheduleId).
                $schedule = [PSCustomObject]@{ Id = $targetScheduleId; Name = 'Weekday Sync' }
                $result = @($schedule | Get-JIMScheduleExecution -Active)

                $result.Count | Should -Be 1
                $result[0].scheduleId | Should -Be $targetScheduleId
            }
        }

        It 'Filters by ScheduleId in the query string when the piped Schedule is used with -Status' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                $schedule = [PSCustomObject]@{ Id = [guid]::NewGuid(); Name = 'Delta Sync' }
                $schedule | Get-JIMScheduleExecution -Status Failed | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like "*scheduleId=$($schedule.Id)*" -and $Endpoint -like '*status=Failed*'
                }
            }
        }

        It 'Sends -Status CompleteWithError by name in the query string (#1787)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMScheduleExecution -Status CompleteWithError | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -like '*status=CompleteWithError*' }
            }
        }

        It 'Still supports direct -ScheduleId with no pipeline input' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $scheduleId = [guid]::NewGuid()
                $matching = [PSCustomObject]@{ id = [guid]::NewGuid(); scheduleId = $scheduleId; status = 'InProgress' }
                $other = [PSCustomObject]@{ id = [guid]::NewGuid(); scheduleId = [guid]::NewGuid(); status = 'Queued' }
                Mock Invoke-JIMApi { @($matching, $other) }

                $result = @(Get-JIMScheduleExecution -ScheduleId $scheduleId -Active)

                $result.Count | Should -Be 1
                $result[0].scheduleId | Should -Be $scheduleId
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMScheduleExecution -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMScheduleExecution -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Stop-JIMScheduleExecution' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Stop-JIMScheduleExecution
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Stop-JIMScheduleExecution -Id ([guid]::NewGuid()) -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Stop-JIMScheduleExecution -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
