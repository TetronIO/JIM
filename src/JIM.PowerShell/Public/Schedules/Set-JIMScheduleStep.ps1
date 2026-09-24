# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMScheduleStep {
    <#
    .SYNOPSIS
        Changes what one step of a Schedule does when it fails.

    .DESCRIPTION
        Sets a Schedule step's failure setting, leaving every other step, and the Schedule itself, as they are.

        A step either follows the Schedule (FollowSchedule), in which case the Schedule's OnStepFailure decides, or
        has a setting of its own (Stop or Continue) that overrides the Schedule's. Use Get-JIMSchedule -Id to see
        each step's Id, its own setting (OnFailure), what it will actually do (ContinueOnFailure) and where that
        comes from (FailureBehaviourSource).

        To change other step fields, send the whole step list through Set-JIMSchedule -Steps.

    .PARAMETER ScheduleId
        The unique identifier (GUID) of the Schedule the step belongs to. A Schedule piped from Get-JIMSchedule binds
        here through its Id.

    .PARAMETER StepId
        The unique identifier (GUID) of the step to change, as shown in the Schedule's Steps.

    .PARAMETER OnFailure
        What the step does to the Schedule when it fails:
        - FollowSchedule: whatever the Schedule is set to do (its OnStepFailure)
        - Stop: stop the Schedule, even if the Schedule is set to continue
        - Continue: carry on with the remaining steps, even if the Schedule is set to stop

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Schedule's change history.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object (the same shape as Get-JIMSchedule -Id).

    .EXAMPLE
        $schedule = Get-JIMSchedule -Id "12345678-..."
        $schedule.Steps | Select-Object Id, StepIndex, OnFailure, ContinueOnFailure, FailureBehaviourSource
        Set-JIMScheduleStep -ScheduleId $schedule.Id -StepId $schedule.Steps[2].Id -OnFailure Stop

        Lists each step's failure behaviour, then sets the third step to stop the Schedule if it fails.

    .EXAMPLE
        Get-JIMSchedule -Name "Nightly HR sync" | Set-JIMScheduleStep -StepId "87654321-..." -OnFailure FollowSchedule -ChangeReason "Back to the Schedule's setting (CHG0102)"

        Returns a step to following its Schedule's setting, recording a reason in the change history.

    .LINK
        Get-JIMSchedule
        Set-JIMSchedule
        Add-JIMScheduleStep
        Remove-JIMScheduleStep
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        # The Id alias is what lets a Schedule piped from Get-JIMSchedule bind here (pipeline binding matches on
        # property name). It also makes -Id a valid spelling of -ScheduleId, which on a step cmdlet could be misread as
        # the step's id; the mandatory -StepId keeps that from going unnoticed, and the docs always spell -ScheduleId.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [guid]$ScheduleId,

        [Parameter(Mandatory)]
        [guid]$StepId,

        [Parameter(Mandatory)]
        [ValidateSet('FollowSchedule', 'Stop', 'Continue')]
        [string]$OnFailure,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        try {
            $schedule = Invoke-JIMApi -Endpoint "/api/v1/schedules/$ScheduleId"
        }
        catch {
            Write-Error "Failed to get Schedule: $_"
            return
        }

        if (-not $schedule) {
            Write-Error "Schedule not found: $ScheduleId"
            return
        }

        $existingSteps = @($schedule.steps | Where-Object { $null -ne $_ })
        if (-not ($existingSteps | Where-Object { [guid]$_.id -eq $StepId })) {
            Write-Error "Step '$StepId' not found in Schedule '$($schedule.name)' ($ScheduleId). Use Get-JIMSchedule -Id $ScheduleId to list its steps and their Ids."
            return
        }

        if ($PSCmdlet.ShouldProcess("Step $StepId of Schedule '$($schedule.name)'", "Set failure behaviour to $OnFailure")) {
            # The update replaces the whole step list, so every step goes back: the target with its new setting, the
            # others exactly as they are (ConvertTo-JIMScheduleStepRequest keeps their ids and failure settings).
            $steps = @(foreach ($step in $existingSteps) {
                if ([guid]$step.id -eq $StepId) {
                    ConvertTo-JIMScheduleStepRequest -Step $step -OnFailure $OnFailure
                }
                else {
                    ConvertTo-JIMScheduleStepRequest -Step $step
                }
            })

            # The Schedule's own settings go back unchanged; onStepFailure is left out, which leaves it unchanged.
            $body = @{
                name = $schedule.name
                description = $schedule.description
                triggerType = $schedule.triggerType
                patternType = $schedule.patternType
                isEnabled = $schedule.isEnabled
                daysOfWeek = $schedule.daysOfWeek
                runTimes = $schedule.runTimes
                intervalValue = $schedule.intervalValue
                intervalUnit = $schedule.intervalUnit
                intervalWindowStart = $schedule.intervalWindowStart
                intervalWindowEnd = $schedule.intervalWindowEnd
                cronExpression = $schedule.cronExpression
                steps = $steps
            }

            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $body.changeReason = $ChangeReason
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedules/$ScheduleId" -Method 'PUT' -Body $body

                Write-Verbose "Set step $StepId of Schedule $ScheduleId to $OnFailure on failure"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Schedule step: $_"
            }
        }
    }
}
