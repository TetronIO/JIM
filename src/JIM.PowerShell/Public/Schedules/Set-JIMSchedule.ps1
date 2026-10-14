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
        How the Schedule starts. Cron runs it automatically on its pattern;
        Manual runs it only when started, from the portal or with
        Start-JIMSchedule.

    .PARAMETER PatternType
        How a Cron Schedule is timed. SpecificTimes runs it at set times of
        day (RunTimes), Interval runs it every few hours or minutes
        (IntervalValue and IntervalUnit), and Custom takes a cron expression
        of your own (CronExpression).

    .PARAMETER DaysOfWeek
        The days the Schedule runs, as numbers from 0 (Sunday) to 6
        (Saturday). For example, @(1,2,3,4,5) for weekdays.

    .PARAMETER RunTimes
        For a SpecificTimes pattern, the times of day to run, in 24-hour
        format, for example @("06:00", "18:00"). Every time must be at the
        same minute past the hour, because a Schedule holds one cron
        expression; use a Custom pattern for times that differ.

    .PARAMETER IntervalValue
        For an Interval pattern, how often to run, counted in IntervalUnit.
        For Hours, no more than 23.

    .PARAMETER IntervalUnit
        For an Interval pattern, the unit of IntervalValue: Hours or Minutes.

    .PARAMETER IntervalWindowStart
        For an hourly Interval pattern, the start of the window the Schedule
        runs in, in 24-hour format. Give IntervalWindowEnd with it.

    .PARAMETER IntervalWindowEnd
        For an hourly Interval pattern, the end of the window the Schedule
        runs in, in 24-hour format.

    .PARAMETER CronExpression
        For a Custom pattern, the cron expression. For SpecificTimes and
        Interval, JIM rebuilds the expression from the pattern, so changing
        RunTimes or the interval retimes the Schedule.

    .PARAMETER OnStepFailure
        What the Schedule does when a step fails. With Stop, the remaining
        steps do not run and the run ends Failed. With Continue, the remaining
        steps run, and a run that carried on past a failure ends
        CompleteWithError. A step with a setting of its own (see
        Set-JIMScheduleStep) overrides it. Omit to leave the setting
        unchanged. A built-in Schedule always stops, and JIM refuses a change
        to it.

    .PARAMETER Steps
        Array of step objects to replace the existing steps. Give each step its failure setting as OnFailure
        (FollowSchedule, Stop or Continue). A step object that carries OnFailure ignores its ContinueOnFailure, so to
        change one step's setting, use Set-JIMScheduleStep, or change the step's OnFailure rather than its
        ContinueOnFailure.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Schedule's change history.

    .PARAMETER PassThru
        Returns the updated Schedule.

    .OUTPUTS
        With -PassThru, the updated Schedule in full; otherwise nothing.

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
    [OutputType('JIM.ScheduleDetailDto')]
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
