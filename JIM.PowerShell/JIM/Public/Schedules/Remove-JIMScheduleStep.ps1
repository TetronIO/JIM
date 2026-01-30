function Remove-JIMScheduleStep {
    <#
    .SYNOPSIS
        Removes a step from a Schedule in JIM.

    .DESCRIPTION
        Removes a step from an existing Schedule by its index position.
        Step indices are 0-based.

    .PARAMETER ScheduleId
        The unique identifier (GUID) of the Schedule to remove the step from.

    .PARAMETER StepIndex
        The index of the step to remove (0-based).

    .PARAMETER Force
        Bypasses confirmation prompts.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object.

    .EXAMPLE
        Remove-JIMScheduleStep -ScheduleId "12345678-..." -StepIndex 0

        Removes the first step from the schedule.

    .EXAMPLE
        Remove-JIMScheduleStep -ScheduleId "12345678-..." -StepIndex 2 -Force -PassThru

        Removes the third step without confirmation and returns the updated schedule.

    .LINK
        Get-JIMSchedule
        Add-JIMScheduleStep
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [guid]$ScheduleId,

        [Parameter(Mandatory)]
        [ValidateRange(0, [int]::MaxValue)]
        [int]$StepIndex,

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($Force -or $PSCmdlet.ShouldProcess("Step $StepIndex from Schedule $ScheduleId", "Remove Schedule Step")) {
            Write-Verbose "Removing step $StepIndex from Schedule: $ScheduleId"

            try {
                # Get existing schedule with steps
                $schedule = Invoke-JIMApi -Endpoint "/api/v1/schedules/$ScheduleId"

                if (-not $schedule) {
                    Write-Error "Schedule not found: $ScheduleId"
                    return
                }

                $existingSteps = if ($schedule.steps) { @($schedule.steps) } else { @() }

                # Find the step(s) to remove by stepIndex
                $stepsToKeep = $existingSteps | Where-Object { $_.stepIndex -ne $StepIndex }

                if ($stepsToKeep.Count -eq $existingSteps.Count) {
                    Write-Warning "No step found at index $StepIndex"
                    return
                }

                # Renumber remaining steps to be sequential
                $renumberedSteps = @()
                $currentIndex = 0
                $lastOriginalIndex = -1

                foreach ($step in ($stepsToKeep | Sort-Object stepIndex)) {
                    if ($step.stepIndex -ne $lastOriginalIndex) {
                        $currentIndex = $renumberedSteps.Count
                        $lastOriginalIndex = $step.stepIndex
                    }

                    $renumberedStep = @{
                        stepIndex = $currentIndex
                        stepType = $step.stepType
                        executionMode = $step.executionMode
                        continueOnFailure = $step.continueOnFailure
                        connectedSystemId = $step.connectedSystemId
                        runProfileId = $step.runProfileId
                    }

                    # Copy optional properties if present
                    if ($step.name) { $renumberedStep.name = $step.name }
                    if ($step.scriptPath) { $renumberedStep.scriptPath = $step.scriptPath }
                    if ($step.arguments) { $renumberedStep.arguments = $step.arguments }
                    if ($step.executablePath) { $renumberedStep.executablePath = $step.executablePath }
                    if ($step.workingDirectory) { $renumberedStep.workingDirectory = $step.workingDirectory }

                    $renumberedSteps += $renumberedStep
                }

                # Build update body
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
                    steps = $renumberedSteps
                }

                $result = Invoke-JIMApi -Endpoint "/api/v1/schedules/$ScheduleId" -Method 'PUT' -Body $body

                Write-Verbose "Removed step from Schedule: $ScheduleId"

                if ($PassThru) {
                    Invoke-JIMApi -Endpoint "/api/v1/schedules/$ScheduleId"
                }
            }
            catch {
                Write-Error "Failed to remove Schedule step: $_"
            }
        }
    }
}
