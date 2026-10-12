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
        The Data Generation Template to run.

    .PARAMETER Name
        The name of the Data Generation Template to run.

    .PARAMETER Wait
        Waits for generation to finish, showing its progress: object counts, throughput and the estimated time
        remaining.

    .PARAMETER Timeout
        How many seconds -Wait waits before it throws an error naming the Activity, which may still be running. Omit
        it to wait until generation finishes.

    .PARAMETER PassThru
        Returns the response to queuing the run.

    .OUTPUTS
        With -PassThru, the response to queuing the run, with the template and where the run got to; otherwise
        nothing. Get-JIMActivity -Follow follows its ActivityId.

        JIM.ExampleDataTemplateExecution
        TemplateId (int): The Data Generation Template that was run.
        Status (string): Queued, or with -Wait the Activity's final status, for example Complete or FailedWithError.

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
    [OutputType('JIM.ExampleDataTemplateExecution')]
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
                $status = 'Queued'

                if ($Wait) {
                    $hasTimeout = $PSBoundParameters.ContainsKey('Timeout')
                    if ($hasTimeout) {
                        Write-Verbose "Waiting for data generation to complete (timeout: ${Timeout}s)"
                    } else {
                        Write-Verbose "Waiting for data generation to complete (no timeout)"
                    }

                    $activityId = $response.activityId

                    $waitParams = @{
                        ActivityId    = "$activityId"
                        ActivityLabel = 'Executing Data Generation Template'
                    }
                    if ($hasTimeout) { $waitParams.Timeout = $Timeout }

                    $finalStatus = Wait-JIMActivityCompletion @waitParams

                    if (-not $finalStatus -and $hasTimeout) {
                        throw "Timeout waiting for data generation after $Timeout seconds. Activity ID: $activityId. The operation may still be running in the background."
                    }
                    if ($finalStatus) { $status = $finalStatus }
                }

                if ($PassThru) {
                    # The API's response, so nothing it reports is dropped, with the template and where the run got to.
                    $response | Add-Member -NotePropertyName 'TemplateId' -NotePropertyValue $Id -Force -PassThru |
                        Add-Member -NotePropertyName 'Status' -NotePropertyValue $status -Force -PassThru
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
