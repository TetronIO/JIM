# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMSchedule {
    <#
    .SYNOPSIS
        Creates a new Schedule in JIM.

    .DESCRIPTION
        Creates a new Schedule that defines an automated synchronisation workflow.
        Schedules can run on a cron schedule or be triggered manually. Use
        Add-JIMScheduleStep to add steps after creation.

    .PARAMETER Name
        The name for the Schedule.

    .PARAMETER Description
        An optional description of what the Schedule does.

    .PARAMETER TriggerType
        How the Schedule starts. Cron runs it automatically on the pattern
        you give; Manual runs it only when started, from the portal or with
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
        same minute past the hour ("06:30" and "18:30", not "06:00" and
        "18:30"), because a Schedule holds one cron expression; use a Custom
        pattern for times that differ.

    .PARAMETER IntervalValue
        For an Interval pattern, how often to run, counted in IntervalUnit.
        For example, 2 with -IntervalUnit Hours runs every two hours. For
        Hours, no more than 23.

    .PARAMETER IntervalUnit
        For an Interval pattern, the unit of IntervalValue: Hours or Minutes.

    .PARAMETER IntervalWindowStart
        For an hourly Interval pattern, the start of the window the Schedule
        runs in, in 24-hour format, for example "08:00". Give
        IntervalWindowEnd with it. An interval in minutes runs all day.

    .PARAMETER IntervalWindowEnd
        For an hourly Interval pattern, the end of the window the Schedule
        runs in, in 24-hour format, for example "18:00".

    .PARAMETER CronExpression
        For a Custom pattern, the cron expression, for example
        "0 6 * * 1-5". For SpecificTimes and Interval, JIM builds the
        expression from the pattern, exactly as the portal's Schedule editor
        does.

    .PARAMETER Enabled
        Enables the Schedule as soon as it is created. Without it, the
        Schedule is created disabled.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Schedule's change history.

    .PARAMETER OnStepFailure
        What the Schedule does when a step fails. With Stop (the default),
        the remaining steps do not run and the run ends Failed. With Continue,
        the remaining steps run, and a run that carried on past a failure ends
        CompleteWithError. A step with a setting of its own
        (Add-JIMScheduleStep -OnFailure) overrides it.

    .PARAMETER PassThru
        Returns the created Schedule.

    .OUTPUTS
        With -PassThru, the created Schedule in full; otherwise nothing.

    .EXAMPLE
        New-JIMSchedule -Name "Delta Sync" -TriggerType Manual

        Creates a manual-only schedule.

    .EXAMPLE
        New-JIMSchedule -Name "Daily Import" -TriggerType Cron -PatternType SpecificTimes -DaysOfWeek @(1,2,3,4,5) -RunTimes @("06:00") -Enabled -PassThru

        Creates an enabled schedule that runs weekdays at 6am.

    .EXAMPLE
        New-JIMSchedule -Name "Hourly Sync" -TriggerType Cron -PatternType Interval -DaysOfWeek @(1,2,3,4,5) -IntervalValue 2 -IntervalUnit Hours -IntervalWindowStart "08:00" -IntervalWindowEnd "18:00"

        Creates a schedule that runs every 2 hours on weekdays between 8am and 6pm.

    .EXAMPLE
        New-JIMSchedule -Name "Custom Schedule" -TriggerType Cron -PatternType Custom -CronExpression "0 */4 * * 1-5"

        Creates a schedule with a custom cron expression (every 4 hours on weekdays).

    .EXAMPLE
        New-JIMSchedule -Name "Nightly HR sync" -TriggerType Manual -OnStepFailure Continue

        Creates a schedule that carries on past a failed step, reporting the run as CompleteWithError.

    .LINK
        Get-JIMSchedule
        Set-JIMSchedule
        Remove-JIMSchedule
        Add-JIMScheduleStep
        Enable-JIMSchedule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.ScheduleDetailDto')]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [string]$Description,

        [Parameter(Mandatory)]
        [ValidateSet('Cron', 'Manual')]
        [string]$TriggerType,

        [Parameter()]
        [ValidateSet('SpecificTimes', 'Interval', 'Custom')]
        [string]$PatternType = 'SpecificTimes',

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
        [string]$IntervalUnit = 'Hours',

        [Parameter()]
        [string]$IntervalWindowStart,

        [Parameter()]
        [string]$IntervalWindowEnd,

        [Parameter()]
        [string]$CronExpression,

        [Parameter()]
        [switch]$Enabled,

        [Parameter()]
        [ValidateSet('Stop', 'Continue')]
        [string]$OnStepFailure,

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

        if ($PSCmdlet.ShouldProcess($Name, "Create Schedule")) {
            Write-Verbose "Creating Schedule: $Name"

            # Enum values are sent as their string names; -TriggerType, -PatternType and
            # -IntervalUnit are ValidateSet-constrained to the exact enum member names.
            # The API rejects numeric ordinals (JsonStringEnumConverter
            # allowIntegerValues:false, PR #1060).
            $body = @{
                name = $Name
                triggerType = $TriggerType
                patternType = $PatternType
                isEnabled = [bool]$Enabled
                steps = @()
            }

            if ($Description) {
                $body.description = $Description
            }

            # Only sent when supplied; the API defaults an absent onStepFailure to Stop.
            if ($OnStepFailure) {
                $body.onStepFailure = $OnStepFailure
            }

            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $body.changeReason = $ChangeReason
            }

            # Add schedule configuration based on trigger type
            if ($TriggerType -eq 'Cron') {
                # Bound, not truthy: PowerShell reads @(0), Sunday alone, as false.
                if ($PSBoundParameters.ContainsKey('DaysOfWeek')) {
                    $body.daysOfWeek = ($DaysOfWeek | Sort-Object) -join ','
                }

                switch ($PatternType) {
                    'SpecificTimes' {
                        if ($PSBoundParameters.ContainsKey('RunTimes')) {
                            $body.runTimes = $RunTimes -join ','
                        }
                    }
                    'Interval' {
                        if ($PSBoundParameters.ContainsKey('IntervalValue')) {
                            $body.intervalValue = $IntervalValue
                        }
                        $body.intervalUnit = $IntervalUnit
                        if ($PSBoundParameters.ContainsKey('IntervalWindowStart')) {
                            $body.intervalWindowStart = $IntervalWindowStart
                        }
                        if ($PSBoundParameters.ContainsKey('IntervalWindowEnd')) {
                            $body.intervalWindowEnd = $IntervalWindowEnd
                        }
                    }
                    'Custom' {
                        if ($CronExpression) {
                            $body.cronExpression = $CronExpression
                        }
                    }
                }
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedules" -Method 'POST' -Body $body

                Write-Verbose "Created Schedule: $($result.id) ($($result.name))"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to create Schedule: $_"
            }
        }
    }
}
