# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
        Shows live progress while waiting (phase, object counts, throughput and estimated
        time remaining), polling the lightweight Activity progress endpoint every 2 seconds.

    .PARAMETER Timeout
        Maximum time in seconds to wait for completion when using -Wait.
        If not specified, waits indefinitely until completion.

    .PARAMETER PreviewActivityId
        The Full Synchronisation preview this run was started after reading. Recorded on the
        run's Activity so "previewed, then run" is auditable rather than a claim. Only a
        Full Synchronisation Run Profile can cite one, and it must be a completed Full
        Synchronisation preview of the same Connected System; anything else is refused and
        the run is not queued.

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
        Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 3 -PreviewActivityId '0198c5e2-7d41-7c3a-9b1e-2f6a8d4c5b10'

        Executes the Full Synchronisation Run Profile ID 3 and records on its Activity that it
        was started after reading that Full Synchronisation preview.

    .EXAMPLE
        Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Wait -Timeout 600

        Executes and waits up to 10 minutes for completion. If the timeout is exceeded,
        an error is thrown.

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

        [ValidateRange(1, [int]::MaxValue)]
        [int]$Timeout,

        [guid]$PreviewActivityId,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

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
            $executeParams = @{
                Endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/run-profiles/$RunProfileId/execute"
                Method   = 'POST'
            }
            # The body is optional on the endpoint; send one only when there is a preview to record.
            if ($PSBoundParameters.ContainsKey('PreviewActivityId')) {
                $executeParams.Body = @{ previewActivityId = $PreviewActivityId }
            }
            $response = Invoke-JIMApi @executeParams

            Write-Verbose "Run Profile queued. ActivityId: $($response.activityId), TaskId: $($response.taskId)"

            if ($Wait) {
                $hasTimeout = $PSBoundParameters.ContainsKey('Timeout')
                if ($hasTimeout) {
                    Write-Verbose "Waiting for Run Profile execution to complete (timeout: ${Timeout}s)"
                } else {
                    Write-Verbose "Waiting for Run Profile execution to complete (no timeout)"
                }

                $activityId = $response.activityId

                $waitParams = @{
                    ActivityId    = "$activityId"
                    ActivityLabel = 'Executing Run Profile'
                }
                if ($hasTimeout) { $waitParams.Timeout = $Timeout }
                # Cooperative abort: a test harness sets this when its error watcher sees an [ERR] line
                # in JIM's logs, so the scenario fails fast instead of polling to the run's natural end.
                if ($env:JIM_RUNPROFILE_ABORT_SENTINEL) { $waitParams.AbortSentinelPath = $env:JIM_RUNPROFILE_ABORT_SENTINEL }

                $finalStatus = Wait-JIMActivityCompletion @waitParams

                if (-not $finalStatus -and $hasTimeout) {
                    throw "Timeout waiting for Run Profile execution after $Timeout seconds. Activity ID: $activityId. The operation may still be running in the background."
                }
            }

            if ($PassThru) {
                $response
            }
        }
        catch {
            # Use throw to propagate as a terminating error so callers with
            # $ErrorActionPreference = "Stop" will see it immediately
            throw "Failed to execute Run Profile: $_"
        }
    }
}
