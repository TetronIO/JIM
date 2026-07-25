# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Invoke-JIMExampleDataTemplate {
    <#
    .SYNOPSIS
        Executes a data generation template to create test data.

    .DESCRIPTION
        Queues a data generation template for execution by the JIM worker service to
        create identity objects in the Metaverse according to the template
        configuration. The task runs asynchronously and is tracked by an Activity;
        monitor progress and completion via Get-JIMActivity, or use the -Wait
        parameter to block until generation completes.

    .PARAMETER Id
        The unique identifier of the template to execute.

    .PARAMETER Name
        The name of the template to execute.

    .PARAMETER Wait
        If specified, waits for the data generation to complete before returning.
        Shows live progress while waiting (object counts, throughput and estimated
        time remaining), polling the lightweight Activity progress endpoint every
        2 seconds.

    .PARAMETER Timeout
        Maximum time in seconds to wait for completion when using -Wait.
        If not specified, waits indefinitely until completion.

    .PARAMETER PassThru
        If specified, returns information about the queued execution, including the
        ActivityId to follow via Get-JIMActivity.

    .OUTPUTS
        If -PassThru is specified, returns the execution information with TemplateId,
        ActivityId, TaskId, Status and Message properties.

    .EXAMPLE
        Invoke-JIMExampleDataTemplate -Id 1

        Executes the data generation template with ID 1.

    .EXAMPLE
        Invoke-JIMExampleDataTemplate -Name 'Test Users'

        Executes the data generation template named 'Test Users'.

    .EXAMPLE
        Invoke-JIMExampleDataTemplate -Id 1 -Wait

        Executes the template and waits for completion with progress display.

    .EXAMPLE
        Get-JIMExampleDataTemplate | Where-Object { $_.name -eq "Test Users" } | Invoke-JIMExampleDataTemplate

        Executes a template from the pipeline.

    .EXAMPLE
        Invoke-JIMExampleDataTemplate -Id 1 -Wait -Timeout 600

        Executes and waits up to 10 minutes for completion. If the timeout is
        exceeded, an error is thrown.

    .EXAMPLE
        Invoke-JIMExampleDataTemplate -Id 1 -PassThru

        Executes the template and returns the queued execution information,
        including the ActivityId.

    .LINK
        Get-JIMExampleDataTemplate
        Get-JIMExampleDataSet
        Get-JIMActivity
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [switch]$Wait,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$Timeout,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Resolve name to ID if using ByName parameter set
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $resolvedTemplate = Resolve-JIMExampleDataTemplate -Name $Name
                $Id = $resolvedTemplate.id
            }
            catch {
                Write-Error $_
                return
            }
        }

        $displayName = if ($Name) { $Name } else { "Template ID: $Id" }

        if ($PSCmdlet.ShouldProcess($displayName, "Execute Data Generation Template")) {
            Write-Verbose "Executing data generation template: $Id"

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates/$Id/execute" -Method 'POST'

                Write-Verbose "Data generation queued. ActivityId: $($response.activityId), TaskId: $($response.taskId)"

                if ($Wait) {
                    $hasTimeout = $PSBoundParameters.ContainsKey('Timeout')
                    if ($hasTimeout) {
                        Write-Verbose "Waiting for data generation to complete (timeout: ${Timeout}s)"
                    } else {
                        Write-Verbose "Waiting for data generation to complete (no timeout)"
                    }

                    $startTime = Get-Date
                    $activityId = $response.activityId
                    $completed = $false
                    $lastStatus = ''

                    $consecutiveAuthFailures = 0
                    $maxAuthFailures = 3

                    while (-not $completed -and (-not $hasTimeout -or ((Get-Date) - $startTime).TotalSeconds -lt $Timeout)) {
                        Start-Sleep -Seconds 2

                        try {
                            # Lightweight progress endpoint (issue #202): status, counts, phase
                            # message, throughput and ETA without the cost of the full detail read.
                            $activityProgress = Invoke-JIMApi -Endpoint "/api/v1/activities/$activityId/progress"

                            # Reset auth failure counter on successful call
                            $consecutiveAuthFailures = 0

                            # Update progress
                            $elapsed = [int]((Get-Date) - $startTime).TotalSeconds
                            $status = $activityProgress.status ?? 'Running'

                            if ($status -ne $lastStatus) {
                                Write-Verbose "Status: $status"
                                $lastStatus = $status
                            }

                            $progressParams = Get-JIMActivityProgressDisplay -Progress $activityProgress -ActivityLabel "Executing Data Generation Template" -ElapsedSeconds $elapsed
                            Write-Progress @progressParams

                            # Check if completed (matches ActivityStatus enum names)
                            if ($status -in @('Complete', 'CompleteWithWarning', 'CompleteWithError', 'FailedWithError', 'Cancelled')) {
                                $completed = $true
                            }
                        }
                        catch {
                            $errorMsg = "$_"

                            # Detect authentication failures and stop polling rather than spamming
                            if ($errorMsg -match 'Authentication failed|session may have expired|API key may be invalid') {
                                $consecutiveAuthFailures++

                                if ($consecutiveAuthFailures -ge $maxAuthFailures) {
                                    Write-Progress -Activity "Executing Data Generation Template" -Completed
                                    throw "Authentication failed while monitoring activity $activityId. The operation was submitted successfully and may still be running on the server. Use Get-JIMActivity -Id $activityId to check its status after re-authenticating with Connect-JIM."
                                }

                                # Brief warning on first/second failure - Invoke-JIMApi may have already
                                # refreshed the token transparently, so give it another chance
                                Write-Warning "Authentication error while checking activity status (attempt $consecutiveAuthFailures of $maxAuthFailures). Retrying..."
                            }
                            else {
                                # Non-auth errors: warn but continue polling
                                Write-Warning "Error checking activity status: $errorMsg"
                            }
                        }
                    }

                    Write-Progress -Activity "Executing Data Generation Template" -Completed

                    if (-not $completed -and $hasTimeout) {
                        throw "Timeout waiting for data generation after $Timeout seconds. Activity ID: $activityId. The operation may still be running in the background."
                    }
                }

                if ($PassThru) {
                    [PSCustomObject]@{
                        TemplateId = $Id
                        ActivityId = $response.activityId
                        TaskId = $response.taskId
                        Status = 'Queued'
                        Message = $response.message
                    }
                }
            }
            catch {
                # Use throw to propagate as a terminating error so callers with
                # $ErrorActionPreference = "Stop" will see it immediately
                throw "Failed to execute data generation template: $_"
            }
        }
    }
}
