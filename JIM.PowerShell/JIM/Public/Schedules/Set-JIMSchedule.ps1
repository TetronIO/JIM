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

    .PARAMETER Steps
        Array of step objects to replace the existing steps.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object.

    .EXAMPLE
        Set-JIMSchedule -Id "12345678-..." -Name "Updated Schedule Name"

        Updates the name of a schedule.

    .EXAMPLE
        Get-JIMSchedule -Id "12345678-..." | Set-JIMSchedule -Description "New description"

        Updates a schedule's description using pipeline input.

    .LINK
        Get-JIMSchedule
        New-JIMSchedule
        Remove-JIMSchedule
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
        [array]$Steps,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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

            # Include existing steps unless new steps provided
            if ($PSBoundParameters.ContainsKey('Steps')) {
                $body.steps = $Steps
            }
            elseif ($existing.steps) {
                $body.steps = $existing.steps
            }
            else {
                $body.steps = @()
            }

            # Apply overrides
            if ($TriggerType) {
                $body.triggerType = switch ($TriggerType) {
                    'Cron' { 0 }
                    'Manual' { 1 }
                }
            }

            if ($PatternType) {
                $body.patternType = switch ($PatternType) {
                    'SpecificTimes' { 0 }
                    'Interval' { 1 }
                    'Custom' { 2 }
                }
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
                $body.intervalUnit = switch ($IntervalUnit) {
                    'Minutes' { 0 }
                    'Hours' { 1 }
                }
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
