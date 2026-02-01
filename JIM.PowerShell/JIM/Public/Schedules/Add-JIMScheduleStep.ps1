function Add-JIMScheduleStep {
    <#
    .SYNOPSIS
        Adds a step to a Schedule in JIM.

    .DESCRIPTION
        Adds a new step to an existing Schedule. Steps define the tasks that
        execute when the schedule runs. Steps can run sequentially or in parallel.

    .PARAMETER ScheduleId
        The unique identifier (GUID) of the Schedule to add the step to.

    .PARAMETER StepType
        The type of step:
        - RunProfile: Execute a Connected System Run Profile

    .PARAMETER ConnectedSystemId
        For RunProfile steps: The ID of the Connected System.

    .PARAMETER ConnectedSystemName
        For RunProfile steps: The name of the Connected System.

    .PARAMETER RunProfileId
        For RunProfile steps: The ID of the Run Profile to execute.

    .PARAMETER RunProfileName
        For RunProfile steps: The name of the Run Profile to execute.

    .PARAMETER Parallel
        If specified, runs this step in parallel with the previous step.
        Otherwise, waits for the previous step to complete.

    .PARAMETER ContinueOnFailure
        If specified, the schedule continues even if this step fails.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object.

    .EXAMPLE
        Add-JIMScheduleStep -ScheduleId "12345678-..." -StepType RunProfile -ConnectedSystemId 1 -RunProfileId 1

        Adds a Run Profile step to the schedule.

    .EXAMPLE
        Add-JIMScheduleStep -ScheduleId "12345678-..." -StepType RunProfile -ConnectedSystemName "HR System" -RunProfileName "Delta Import" -PassThru

        Adds a step using names instead of IDs.

    .EXAMPLE
        $schedule = Get-JIMSchedule -Name "Delta Sync"
        Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile -ConnectedSystemName "HR" -RunProfileName "Import"
        Add-JIMScheduleStep -ScheduleId $schedule.id -StepType RunProfile -ConnectedSystemName "Badge" -RunProfileName "Import" -Parallel

        Adds two import steps that run in parallel.

    .LINK
        Get-JIMSchedule
        New-JIMSchedule
        Remove-JIMScheduleStep
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [guid]$ScheduleId,

        [Parameter(Mandatory)]
        [ValidateSet('RunProfile')]
        [string]$StepType,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$RunProfileId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$RunProfileName,

        [switch]$Parallel,

        [switch]$ContinueOnFailure,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Resolve names to IDs if using ByName parameter set
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $cs = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
                $ConnectedSystemId = $cs.id

                $runProfiles = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/run-profiles"
                $runProfile = $runProfiles | Where-Object { $_.name -eq $RunProfileName }

                if (-not $runProfile) {
                    Write-Error "Run Profile '$RunProfileName' not found for Connected System '$ConnectedSystemName'"
                    return
                }

                $RunProfileId = $runProfile.id
            }
            catch {
                Write-Error "Failed to resolve names: $_"
                return
            }
        }

        if ($PSCmdlet.ShouldProcess($ScheduleId, "Add Schedule Step")) {
            Write-Verbose "Adding step to Schedule: $ScheduleId"

            try {
                # Get existing schedule with steps
                $schedule = Invoke-JIMApi -Endpoint "/api/v1/schedules/$ScheduleId"

                if (-not $schedule) {
                    Write-Error "Schedule not found: $ScheduleId"
                    return
                }

                # Determine step index
                $existingSteps = if ($schedule.steps) { $schedule.steps } else { @() }
                $maxStepIndex = if ($existingSteps.Count -gt 0) {
                    ($existingSteps | Measure-Object -Property stepIndex -Maximum).Maximum
                } else { -1 }

                # If parallel, use same step index as previous; otherwise increment
                $newStepIndex = if ($Parallel -and $maxStepIndex -ge 0) {
                    $maxStepIndex
                } else {
                    $maxStepIndex + 1
                }

                # Build the new step
                $newStep = @{
                    stepIndex = [int]$newStepIndex
                    stepType = 0  # RunProfile
                    executionMode = if ($Parallel) { 1 } else { 0 }  # 0=Sequential, 1=ParallelWithPrevious
                    continueOnFailure = [bool]$ContinueOnFailure
                    connectedSystemId = [int]$ConnectedSystemId
                    runProfileId = [int]$RunProfileId
                }

                # Convert existing steps to proper format for API
                $convertedSteps = @()
                foreach ($step in $existingSteps) {
                    $convertedSteps += @{
                        id = $step.id
                        stepIndex = [int]$step.stepIndex
                        stepType = if ($step.stepType -is [int]) { $step.stepType } else { 0 }
                        executionMode = if ($step.executionMode -is [int]) { $step.executionMode } else { 0 }
                        continueOnFailure = [bool]$step.continueOnFailure
                        connectedSystemId = if ($step.connectedSystemId) { [int]$step.connectedSystemId } else { $null }
                        runProfileId = if ($step.runProfileId) { [int]$step.runProfileId } else { $null }
                        name = $step.name
                        scriptPath = $step.scriptPath
                        arguments = $step.arguments
                        executablePath = $step.executablePath
                        workingDirectory = $step.workingDirectory
                    }
                }

                # Build update body with existing steps plus new one
                $allSteps = $convertedSteps + @($newStep)

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
                    steps = $allSteps
                }

                $result = Invoke-JIMApi -Endpoint "/api/v1/schedules/$ScheduleId" -Method 'PUT' -Body $body

                Write-Verbose "Added step to Schedule: $ScheduleId"

                if ($PassThru) {
                    Invoke-JIMApi -Endpoint "/api/v1/schedules/$ScheduleId"
                }
            }
            catch {
                Write-Error "Failed to add Schedule step: $_"
            }
        }
    }
}
