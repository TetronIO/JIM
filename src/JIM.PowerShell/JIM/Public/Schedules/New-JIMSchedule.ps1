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
        Optional description of what this schedule does.

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
        Example: @(1,2,3,4,5) for weekdays.

    .PARAMETER RunTimes
        For SpecificTimes pattern: Array of times to run each day.
        Example: @("06:00", "12:00", "18:00")

    .PARAMETER IntervalValue
        For Interval pattern: The interval value (e.g., 2 for "every 2 hours").

    .PARAMETER IntervalUnit
        For Interval pattern: The interval unit (Hours or Minutes).

    .PARAMETER IntervalWindowStart
        For Interval pattern: Start of the time window (e.g., "06:00").

    .PARAMETER IntervalWindowEnd
        For Interval pattern: End of the time window (e.g., "18:00").

    .PARAMETER CronExpression
        For Custom pattern: The cron expression (e.g., "0 6 * * 1-5").

    .PARAMETER Enabled
        Whether the schedule should be enabled after creation. Default is $false.

    .PARAMETER PassThru
        If specified, returns the created Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the created Schedule object.

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

    .LINK
        Get-JIMSchedule
        Set-JIMSchedule
        Remove-JIMSchedule
        Add-JIMScheduleStep
        Enable-JIMSchedule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
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

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Schedule")) {
            Write-Verbose "Creating Schedule: $Name"

            # Map TriggerType to API enum
            $triggerTypeValue = switch ($TriggerType) {
                'Cron' { 0 }
                'Manual' { 1 }
            }

            # Map PatternType to API enum
            $patternTypeValue = switch ($PatternType) {
                'SpecificTimes' { 0 }
                'Interval' { 1 }
                'Custom' { 2 }
            }

            # Map IntervalUnit to API enum
            $intervalUnitValue = switch ($IntervalUnit) {
                'Minutes' { 0 }
                'Hours' { 1 }
            }

            $body = @{
                name = $Name
                triggerType = $triggerTypeValue
                patternType = $patternTypeValue
                isEnabled = [bool]$Enabled
                steps = @()
            }

            if ($Description) {
                $body.description = $Description
            }

            # Add schedule configuration based on trigger type
            if ($TriggerType -eq 'Cron') {
                if ($DaysOfWeek) {
                    $body.daysOfWeek = ($DaysOfWeek | Sort-Object) -join ','
                }

                switch ($PatternType) {
                    'SpecificTimes' {
                        if ($RunTimes) {
                            $body.runTimes = $RunTimes -join ','
                        }
                    }
                    'Interval' {
                        if ($IntervalValue) {
                            $body.intervalValue = $IntervalValue
                        }
                        $body.intervalUnit = $intervalUnitValue
                        if ($IntervalWindowStart) {
                            $body.intervalWindowStart = $IntervalWindowStart
                        }
                        if ($IntervalWindowEnd) {
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
