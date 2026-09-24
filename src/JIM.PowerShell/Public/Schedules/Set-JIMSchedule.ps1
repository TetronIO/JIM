# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMSchedule {
    <#
    .SYNOPSIS
        Updates an existing Schedule in JIM.

    .DESCRIPTION
        Updates the configuration of an existing Schedule. Note that this replaces
        the schedule steps with any steps provided. To preserve existing steps,
        use Get-JIMSchedule -IncludeSteps and modify the steps array.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule to update.

    .PARAMETER Name
        The new name for the Schedule.

    .PARAMETER Description
        The new description for the Schedule.

    .PARAMETER TriggerType
        How the schedule is triggered:
        - Cron: Runs automatically based on a schedule
        - Manual: Only runs when triggered manually

    .PARAMETER PatternType
        The scheduling pattern type (for Cron triggers):
        - SpecificTimes: Run at specific times each day
        - Interval: Run at regular intervals
        - Custom: Use a custom cron expression

    .PARAMETER DaysOfWeek
        Days of the week to run (0=Sunday, 1=Monday, ..., 6=Saturday).

    .PARAMETER RunTimes
        For SpecificTimes pattern: Array of times to run each day.

    .PARAMETER IntervalValue
        For Interval pattern: The interval value.

    .PARAMETER IntervalUnit
        For Interval pattern: The interval unit (Hours or Minutes).

    .PARAMETER IntervalWindowStart
        For Interval pattern: Start of the time window.

    .PARAMETER IntervalWindowEnd
        For Interval pattern: End of the time window.

    .PARAMETER CronExpression
        For Custom pattern: The cron expression.

    .PARAMETER OnStepFailure
        What the Schedule does when a step fails, for every step that follows the Schedule:
        - Stop: the remaining steps do not run, and the run ends Failed
        - Continue: the remaining steps run, and a run that carried on past a failure ends CompleteWithError
        A step with a setting of its own (see Set-JIMScheduleStep) overrides it. Omit to leave the setting unchanged.
        A built-in Schedule's setting cannot be changed.

    .PARAMETER Steps
        Array of step objects to replace the existing steps. Give each step its failure setting as OnFailure
        (FollowSchedule, Stop or Continue). A step object that carries OnFailure ignores its ContinueOnFailure, so to
        change one step's setting, use Set-JIMScheduleStep, or change the step's OnFailure rather than its
        ContinueOnFailure.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Schedule's change history.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object.

    .EXAMPLE
        Set-JIMSchedule -Id "12345678-..." -Name "Updated Schedule Name"

        Updates the name of a schedule.

    .EXAMPLE
        Set-JIMSchedule -Id "12345678-..." -IntervalValue 15 -ChangeReason "Tighter sync cadence (CHG0042)"

        Re-times the schedule and records a reason against its change history.

    .EXAMPLE
        Set-JIMSchedule -Id "12345678-..." -OnStepFailure Continue

        Lets the Schedule carry on past a failed step, for every step that follows the Schedule.

    .EXAMPLE
        Get-JIMSchedule -Id "12345678-..." | Set-JIMSchedule -Description "New description"

        Updates a schedule's description using pipeline input.

    .LINK
        Get-JIMSchedule
        New-JIMSchedule
        Remove-JIMSchedule
        Set-JIMScheduleStep
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ScheduleId')]
        [guid]$Id,

        [Parameter()]
        [string]$Name,

        [Parameter()]
        [string]$Description,

        [Parameter()]
        [ValidateSet('Cron', 'Manual')]
        [string]$TriggerType,

        [Parameter()]
        [ValidateSet('SpecificTimes', 'Interval', 'Custom')]
        [string]$PatternType,

        [Parameter()]
        [ValidateRange(0, 6)]
        [int[]]$DaysOfWeek,

        [Parameter()]
        [string[]]$RunTimes,

        [Parameter()]
        [ValidateRange(1, 59)]
        [int]$IntervalValue,

        [Parameter()]
        [ValidateSet('Hours', 'Minutes')]
        [string]$IntervalUnit,

        [Parameter()]
        [string]$IntervalWindowStart,

        [Parameter()]
        [string]$IntervalWindowEnd,

        [Parameter()]
        [string]$CronExpression,

        [Parameter()]
        [ValidateSet('Stop', 'Continue')]
        [string]$OnStepFailure,

        [Parameter()]
        [array]$Steps,

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

        if ($PSCmdlet.ShouldProcess($Id, "Update Schedule")) {
            Write-Verbose "Updating Schedule: $Id"

            # Get existing schedule to merge with updates
            try {
                $existing = Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id"
            }
            catch {
                Write-Error "Failed to get existing Schedule: $_"
                return
            }

            # Build update body, starting with existing values
            $body = @{
                name = if ($Name) { $Name } else { $existing.name }
                description = if ($PSBoundParameters.ContainsKey('Description')) { $Description } else { $existing.description }
                triggerType = $existing.triggerType
                patternType = $existing.patternType
                isEnabled = $existing.isEnabled
                daysOfWeek = $existing.daysOfWeek
                runTimes = $existing.runTimes
                intervalValue = $existing.intervalValue
                intervalUnit = $existing.intervalUnit
                intervalWindowStart = $existing.intervalWindowStart
                intervalWindowEnd = $existing.intervalWindowEnd
                cronExpression = $existing.cronExpression
            }

            # Include existing steps unless new steps provided. Existing steps go back with their ids and failure
            # settings (ConvertTo-JIMScheduleStepRequest), so changing the Schedule changes none of its steps.
            if ($PSBoundParameters.ContainsKey('Steps')) {
                $body.steps = $Steps
            }
            elseif ($existing.steps) {
                $body.steps = @($existing.steps | ConvertTo-JIMScheduleStepRequest)
            }
            else {
                $body.steps = @()
            }

            # Apply overrides. Enum values are sent as their string names (the parameters
            # are ValidateSet-constrained to the exact enum member names); the API rejects
            # numeric ordinals (JsonStringEnumConverter allowIntegerValues:false, PR #1060).
            if ($TriggerType) {
                $body.triggerType = $TriggerType
            }

            if ($PatternType) {
                $body.patternType = $PatternType
            }

            if ($PSBoundParameters.ContainsKey('DaysOfWeek')) {
                $body.daysOfWeek = ($DaysOfWeek | Sort-Object) -join ','
            }

            if ($PSBoundParameters.ContainsKey('RunTimes')) {
                $body.runTimes = $RunTimes -join ','
            }

            if ($PSBoundParameters.ContainsKey('IntervalValue')) {
                $body.intervalValue = $IntervalValue
            }

            if ($IntervalUnit) {
                $body.intervalUnit = $IntervalUnit
            }

            if ($PSBoundParameters.ContainsKey('IntervalWindowStart')) {
                $body.intervalWindowStart = $IntervalWindowStart
            }

            if ($PSBoundParameters.ContainsKey('IntervalWindowEnd')) {
                $body.intervalWindowEnd = $IntervalWindowEnd
            }

            if ($PSBoundParameters.ContainsKey('CronExpression')) {
                $body.cronExpression = $CronExpression
            }

            # Only sent when supplied: an absent onStepFailure leaves the stored setting unchanged.
            if ($OnStepFailure) {
                $body.onStepFailure = $OnStepFailure
            }

            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $body.changeReason = $ChangeReason
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id" -Method 'PUT' -Body $body

                Write-Verbose "Updated Schedule: $Id"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Schedule: $_"
            }
        }
    }
}
