function Start-JIMSchedule {
    <#
    .SYNOPSIS
        Manually triggers a Schedule execution in JIM.

    .DESCRIPTION
        Manually starts a Schedule execution, regardless of its trigger type
        or enabled state. The schedule runs through all its configured steps.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule to start.

    .PARAMETER Wait
        If specified, waits for the schedule execution to complete and returns
        the execution result.

    .PARAMETER Timeout
        Maximum time to wait for completion when -Wait is specified.
        Default is 30 minutes.

    .PARAMETER PassThru
        If specified, returns the ScheduleExecution object.

    .OUTPUTS
        If -PassThru or -Wait is specified, returns the ScheduleExecution object.

    .EXAMPLE
        Start-JIMSchedule -Id "12345678-1234-1234-1234-123456789012"

        Triggers the specified Schedule to run.

    .EXAMPLE
        Get-JIMSchedule -Name "Delta Sync" | Start-JIMSchedule -PassThru

        Triggers the "Delta Sync" schedule and returns the execution object.

    .EXAMPLE
        Start-JIMSchedule -Id "12345678-..." -Wait -Timeout ([TimeSpan]::FromMinutes(60))

        Triggers the schedule and waits up to 60 minutes for it to complete.

    .LINK
        Get-JIMSchedule
        Get-JIMScheduleExecution
        Stop-JIMScheduleExecution
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ScheduleId')]
        [guid]$Id,

        [switch]$Wait,

        [TimeSpan]$Timeout = [TimeSpan]::FromMinutes(30),

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ShouldProcess($Id, "Start Schedule")) {
            Write-Verbose "Starting Schedule: $Id"

            try {
                $execution = Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id/run" -Method 'POST'

                Write-Verbose "Started Schedule Execution: $($execution.id)"

                if ($Wait) {
                    $startTime = [DateTime]::UtcNow
                    $pollInterval = 5  # seconds

                    Write-Verbose "Waiting for execution to complete (timeout: $Timeout)..."

                    while ($true) {
                        $currentExecution = Invoke-JIMApi -Endpoint "/api/v1/schedule-executions/$($execution.id)"

                        # Check if completed (status 2=Completed, 3=Failed, 4=Cancelled)
                        if ($currentExecution.status -ge 2) {
                            Write-Verbose "Execution completed with status: $($currentExecution.status)"
                            $execution = $currentExecution
                            break
                        }

                        # Check timeout
                        $elapsed = [DateTime]::UtcNow - $startTime
                        if ($elapsed -ge $Timeout) {
                            Write-Warning "Timeout waiting for schedule execution to complete. Execution ID: $($execution.id)"
                            break
                        }

                        # Display progress
                        $progressPercent = if ($currentExecution.totalSteps -gt 0) {
                            [int](($currentExecution.currentStepIndex / $currentExecution.totalSteps) * 100)
                        } else { 0 }

                        Write-Progress -Activity "Waiting for Schedule Execution" `
                            -Status "Step $($currentExecution.currentStepIndex + 1) of $($currentExecution.totalSteps)" `
                            -PercentComplete $progressPercent

                        Start-Sleep -Seconds $pollInterval
                    }

                    Write-Progress -Activity "Waiting for Schedule Execution" -Completed
                    $execution
                }
                elseif ($PassThru) {
                    $execution
                }
            }
            catch {
                Write-Error "Failed to start Schedule: $_"
            }
        }
    }
}
