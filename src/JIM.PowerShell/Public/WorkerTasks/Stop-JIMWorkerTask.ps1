# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Stop-JIMWorkerTask {
    <#
    .SYNOPSIS
        Cancels a Worker Task in JIM.

    .DESCRIPTION
        Requests cancellation of a queued or in-progress Worker Task. For a task actively
        being processed, this signals the worker to stop and clean up gracefully; the task
        remains visible with CancellationRequested status until the worker picks this up.
        For a queued task, it is cancelled and removed immediately. Cancellation completes
        asynchronously; JIM returns as soon as the request has been accepted.

    .PARAMETER Id
        The unique identifier (GUID) of the Worker Task to cancel.

    .PARAMETER Force
        Bypasses confirmation prompts.

    .OUTPUTS
        None.

    .EXAMPLE
        Stop-JIMWorkerTask -Id "12345678-1234-1234-1234-123456789012"

        Requests cancellation of the specified Worker Task (with confirmation).

    .EXAMPLE
        Get-JIMWorkerTask | Stop-JIMWorkerTask -Force

        Requests cancellation of every in-flight Worker Task without confirmation.

    .LINK
        Get-JIMWorkerTask
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($Force -or $PSCmdlet.ShouldProcess($Id, "Cancel Worker Task")) {
            Write-Verbose "Requesting cancellation of Worker Task: $Id"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/worker-tasks/$Id/cancel" -Method 'POST' | Out-Null
                Write-Verbose "Requested cancellation of Worker Task: $Id"
            }
            catch {
                Write-Error "Failed to cancel Worker Task: $_"
            }
        }
    }
}
