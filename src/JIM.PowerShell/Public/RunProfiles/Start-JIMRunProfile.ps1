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
        The Connected System the Run Profile belongs to.

    .PARAMETER ConnectedSystemName
        The name of the Connected System the Run Profile belongs to. Must be an exact match.

    .PARAMETER RunProfileId
        The Run Profile to execute.

    .PARAMETER RunProfileName
        The name of the Run Profile to execute. Must be an exact match.

    .PARAMETER Wait
        Waits for the execution to finish, showing its progress: the step it is on, object counts, throughput and
        the estimated time remaining.

    .PARAMETER Timeout
        How many seconds -Wait waits before it throws an error naming the Activity, which may still be running. Omit
        it to wait until the execution finishes.

    .PARAMETER PreviewActivityId
        The Full Synchronisation preview this run was started after reading, recorded on the run's Activity so that
        it shows what informed the run. Only a Full Synchronisation Run Profile can cite one, and it must be a
        completed Full Synchronisation preview of the same Connected System; anything else is refused and the run is
        not queued. A preview that a later run or configuration change has overtaken is still recorded, and the
        cmdlet warns what overtook it.

    .PARAMETER PassThru
        Returns the response to queuing the run.

    .OUTPUTS
        With -PassThru, the response to queuing the run; Get-JIMActivity -Follow follows its ActivityId. Otherwise
        nothing. Warnings in the response are also written as PowerShell warnings.

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
    [OutputType('JIM.RunProfileExecutionResponse')]
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

            # What the server wants the caller to know about a run it queued: a partition warning, or the preview it
            # cites having been out of date, which its Activity records.
            foreach ($warning in @($response.warnings)) {
                if ($warning) { Write-Warning $warning }
            }

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
