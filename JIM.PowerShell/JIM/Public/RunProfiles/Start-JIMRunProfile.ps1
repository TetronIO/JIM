function Start-JIMRunProfile {
    <#
    .SYNOPSIS
        Executes a Run Profile to trigger a synchronisation operation.

    .DESCRIPTION
        Queues a synchronisation task (Full Import, Delta Import, Full Sync, Delta Sync,
        or Export) for execution by the JIM worker service. The task runs asynchronously
        and can be monitored via Get-JIMActivity.

        Use the -Wait parameter to block until the operation completes.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ConnectedSystemName
        The name of the Connected System. Must be an exact match.

    .PARAMETER RunProfileId
        The unique identifier of the Run Profile to execute.
        Alias: Id (for pipeline input from Get-JIMRunProfile).

    .PARAMETER RunProfileName
        The name of the Run Profile to execute. Must be an exact match.

    .PARAMETER Wait
        If specified, waits for the Run Profile execution to complete before returning.
        Shows progress while waiting.

    .PARAMETER Timeout
        Maximum time in seconds to wait for completion when using -Wait.
        Defaults to 300 seconds (5 minutes).

    .PARAMETER PassThru
        If specified, returns the execution result object.

    .OUTPUTS
        If -PassThru is specified, returns the execution response with ActivityId and TaskId.

    .EXAMPLE
        Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1

        Executes Run Profile ID 1 for Connected System ID 1.

    .EXAMPLE
        Start-JIMRunProfile -ConnectedSystemName 'Contoso AD' -RunProfileName 'Full Import'

        Executes the 'Full Import' Run Profile for the 'Contoso AD' Connected System.

    .EXAMPLE
        Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Wait

        Executes the Run Profile and waits for completion with progress display.

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemId 1 | Where-Object { $_.name -eq "Full Import" } | Start-JIMRunProfile -Wait

        Executes the "Full Import" Run Profile and waits for completion.

    .EXAMPLE
        Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Wait -Timeout 600

        Executes and waits up to 10 minutes for completion.

    .LINK
        Get-JIMRunProfile
        Get-JIMActivity
        Get-JIMActivityStats
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ByIdAndName', ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [Parameter(Mandatory, ParameterSetName = 'ByNameAndId')]
        [string]$ConnectedSystemName,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ByNameAndId')]
        [Alias('Id')]
        [int]$RunProfileId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [Parameter(Mandatory, ParameterSetName = 'ByIdAndName')]
        [string]$RunProfileName,

        [switch]$Wait,

        [ValidateRange(1, 3600)]
        [int]$Timeout = 300,

        [switch]$PassThru
    )

    process {
        # Resolve ConnectedSystemName to ConnectedSystemId if specified
        if ($PSBoundParameters.ContainsKey('ConnectedSystemName')) {
            $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
            $ConnectedSystemId = $connectedSystem.id
        }

        # Resolve RunProfileName to RunProfileId if specified
        if ($PSBoundParameters.ContainsKey('RunProfileName')) {
            Write-Verbose "Resolving Run Profile name: $RunProfileName"
            $profiles = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/run-profiles"
            $matchingProfile = @($profiles | Where-Object { $_.name -eq $RunProfileName })

            if ($matchingProfile.Count -eq 0) {
                Write-Error "Run Profile not found: '$RunProfileName' for Connected System ID $ConnectedSystemId"
                return
            }

            if ($matchingProfile.Count -gt 1) {
                Write-Error "Multiple Run Profiles found with name '$RunProfileName'. Use -RunProfileId to specify the exact profile."
                return
            }

            $RunProfileId = $matchingProfile[0].id
        }

        Write-Verbose "Executing Run Profile ID $RunProfileId for Connected System ID $ConnectedSystemId"

        try {
            $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/run-profiles/$RunProfileId/execute" -Method 'POST'

            Write-Verbose "Run Profile queued. ActivityId: $($response.activityId), TaskId: $($response.taskId)"

            if ($Wait) {
                Write-Verbose "Waiting for Run Profile execution to complete (timeout: ${Timeout}s)"

                $startTime = Get-Date
                $activityId = $response.activityId
                $completed = $false
                $lastStatus = ''

                while (-not $completed -and ((Get-Date) - $startTime).TotalSeconds -lt $Timeout) {
                    Start-Sleep -Seconds 2

                    try {
                        $activity = Invoke-JIMApi -Endpoint "/api/v1/activities/$activityId"

                        # Update progress
                        $elapsed = [int]((Get-Date) - $startTime).TotalSeconds
                        $status = $activity.status ?? 'Running'

                        if ($status -ne $lastStatus) {
                            Write-Verbose "Status: $status"
                            $lastStatus = $status
                        }

                        $progressParams = @{
                            Activity = "Executing Run Profile"
                            Status = "$status - Elapsed: ${elapsed}s"
                            PercentComplete = -1  # Indeterminate
                        }

                        # Try to get stats for better progress info
                        try {
                            $stats = Invoke-JIMApi -Endpoint "/api/v1/activities/$activityId/stats"
                            if ($stats.totalItems -gt 0) {
                                $percent = [int](($stats.processedItems / $stats.totalItems) * 100)
                                $progressParams.Status = "$status - $($stats.processedItems) of $($stats.totalItems) items"
                                $progressParams.PercentComplete = $percent
                            }
                        } catch {
                            # Stats not available, continue with indeterminate progress
                        }

                        Write-Progress @progressParams

                        # Check if completed (matches ActivityStatus enum names)
                        if ($status -in @('Complete', 'CompleteWithWarning', 'CompleteWithError', 'FailedWithError', 'Cancelled')) {
                            $completed = $true
                        }
                    }
                    catch {
                        Write-Warning "Error checking activity status: $_"
                    }
                }

                Write-Progress -Activity "Executing Run Profile" -Completed

                if (-not $completed) {
                    Write-Warning "Timeout waiting for Run Profile execution. Activity ID: $activityId"
                }
            }

            if ($PassThru) {
                $response
            }
        }
        catch {
            Write-Error "Failed to execute Run Profile: $_"
        }
    }
}
