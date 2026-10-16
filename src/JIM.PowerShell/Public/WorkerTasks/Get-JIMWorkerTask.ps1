# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMWorkerTask {
    <#
    .SYNOPSIS
        Gets Worker Tasks from JIM.

    .DESCRIPTION
        Retrieves currently queued, processing, or cancellation-requested Worker Tasks.
        Worker Tasks are ephemeral: once a task completes, its record is deleted (the
        associated Activity is the durable audit record), so this cmdlet only ever
        returns in-flight work.

    .PARAMETER Id
        The unique identifier (GUID) of a specific Worker Task to retrieve.

    .PARAMETER Page
        The page of results to return.

    .PARAMETER PageSize
        The number of Worker Tasks on each page.

    .OUTPUTS
        Each Worker Task's header, with one property the module adds.

        JIM.WorkerTaskHeader
        StepDisplay (string): The step the run is on, as one sentence such as "Step 3 of 7: Saving changes". It
            is the sentence the portal shows in Admin > Operations > Queue, and the one Start-JIMRunProfile -Wait
            shows live, so a run reads the same wherever it is watched from. Empty for a task that is not a Run
            Profile execution, since those record no steps.

    .EXAMPLE
        Get-JIMWorkerTask

        Gets the first page of in-flight Worker Tasks.

    .EXAMPLE
        Get-JIMWorkerTask -Id "12345678-1234-1234-1234-123456789012"

        Gets a specific Worker Task.

    .LINK
        Stop-JIMWorkerTask
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.WorkerTaskHeader')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            Write-Verbose "Getting Worker Task $Id"
            Add-JIMWorkerTaskStepDisplay -Task (Invoke-JIMApi -Endpoint "/api/v1/worker-tasks/$Id")
            return
        }

        Write-Verbose "Getting Worker Tasks (Page: $Page, PageSize: $PageSize)"
        $response = Invoke-JIMApi -Endpoint "/api/v1/worker-tasks?page=$Page&pageSize=$PageSize"
        foreach ($item in $response.items) {
            Add-JIMWorkerTaskStepDisplay -Task $item
        }
    }
}
