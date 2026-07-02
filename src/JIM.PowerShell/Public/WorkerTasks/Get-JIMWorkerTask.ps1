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
        Page number for paginated results. Defaults to 1. Not applicable when -Id is specified.

    .PARAMETER PageSize
        Number of items per page (1-100). Defaults to 50. Not applicable when -Id is specified.

    .OUTPUTS
        PSCustomObject representing Worker Task header(s).

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
    [OutputType([PSCustomObject])]
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
            Invoke-JIMApi -Endpoint "/api/v1/worker-tasks/$Id"
            return
        }

        Write-Verbose "Getting Worker Tasks (Page: $Page, PageSize: $PageSize)"
        $response = Invoke-JIMApi -Endpoint "/api/v1/worker-tasks?page=$Page&pageSize=$PageSize"
        foreach ($item in $response.items) {
            $item
        }
    }
}
