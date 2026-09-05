# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Stop-JIMPendingPasswordChange {
    <#
    .SYNOPSIS
        Stops JIM delivering queued password changes to Connected Systems.

    .DESCRIPTION
        Records that JIM should stop trying to deliver every matching queued password change.

        The rows are kept, marked Cancelled, with who cancelled them and when. They are not deleted, and that is
        the point: the person's password is still divergent on that Connected System, and the cancelled row is
        the only thing that says so. Retention removes them on the same schedule as any other finished change.

        A cancelled change is not final. Resume-JIMPendingPasswordChange puts it back on the queue, provided it
        has not expired in the meantime.

        Applies to changes JIM has not finished with: Pending and Parked. An Expired or already Cancelled change
        is left alone rather than having its recorded outcome overwritten.

        Whatever it covers, this is one request and one Activity, however many changes are piped in.

    .PARAMETER Id
        The unique identifiers of the queued changes to cancel. Accepts queued changes from the pipeline.
        Combines with the other criteria rather than replacing them, so "-Status Parked" alongside piped changes
        means "these, if they are still parked": a change delivered since you listed it is not cancelled.

    .PARAMETER ConnectedSystemId
        Cancel the changes queued for one Connected System. Accepts a Connected System from the pipeline.

    .PARAMETER Status
        Cancel only changes in this state: Pending or Parked.

    .PARAMETER FailureReason
        Cancel only changes whose last attempt failed this way.

    .PARAMETER MetaverseObjectId
        Cancel only one identity's queued changes.

    .PARAMETER Search
        Cancel only changes matching this free-text search over the identity and Connected System names.

    .PARAMETER EntireQueue
        Cancel every queued password change in the deployment. Required when nothing else narrows the request,
        so a command that names no criteria cannot empty the queue by accident.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        PSCustomObject with an AffectedCount property: how many changes were cancelled.

    .EXAMPLE
        Stop-JIMPendingPasswordChange -ConnectedSystemId 7

        Cancels every queued password change for Connected System 7, with confirmation. The command for a system
        being decommissioned, where the queued passwords will never be wanted.

    .EXAMPLE
        Get-JIMPendingPasswordChange -Status Parked -ConnectedSystemId 7

        Run this first. It shows exactly which changes the command above would cancel; each one leaves somebody's
        password unchanged on that system.

    .EXAMPLE
        Get-JIMPendingPasswordChange -Status Expired | Stop-JIMPendingPasswordChange -Force

        Does nothing, and returns AffectedCount 0. An expired change already has an outcome, and cancelling it
        would overwrite what actually happened to it.

    .EXAMPLE
        Stop-JIMPendingPasswordChange -MetaverseObjectId 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -WhatIf

        Shows what would be cancelled for one person without cancelling it.

    .LINK
        Get-JIMPendingPasswordChange
        Resume-JIMPendingPasswordChange
        Set-JIMMetaverseObjectPassword
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(ValueFromPipelineByPropertyName)]
        [guid[]]$Id,

        [Parameter(ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter()]
        [ValidateSet('Pending', 'Parked', 'Expired', 'Cancelled')]
        [string]$Status,

        [Parameter()]
        [ValidateSet('None', 'Transient', 'ConfigurationFault', 'PolicyRejection', 'TargetObjectNotFound', 'UnsupportedOperation')]
        [string]$FailureReason,

        [Parameter()]
        [guid]$MetaverseObjectId,

        [Parameter()]
        [string]$Search,

        [Parameter()]
        [switch]$EntireQueue,

        [Parameter()]
        [switch]$Force
    )

    begin {
        # Collected here and acted on in end, so a piped selection becomes one request and one Activity rather
        # than one per row. See Resume-JIMPendingPasswordChange, which does the same for the same reason.
        $collectedIds = [System.Collections.Generic.List[guid]]::new()
    }

    process {
        if ($Id) {
            foreach ($changeId in $Id) {
                $collectedIds.Add($changeId)
            }
        }
    }

    end {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # An empty pipeline is a successful no-op: there was nothing matching to cancel.
        if ($collectedIds.Count -eq 0 -and $PSCmdlet.MyInvocation.ExpectingInput -and -not $PSBoundParameters.ContainsKey('ConnectedSystemId')) {
            Write-Verbose "Nothing reached Stop-JIMPendingPasswordChange from the pipeline; there is nothing to cancel."
            return
        }

        $target = if ($collectedIds.Count -gt 0) {
            if ($collectedIds.Count -eq 1) { "1 queued password change" } else { "$($collectedIds.Count) queued password changes" }
        }
        elseif ($EntireQueue) { "every queued password change" }
        else { "the matching queued password changes" }

        if (-not $Force -and -not $PSCmdlet.ShouldProcess($target, "Stop delivering, leaving the password unchanged on the target")) {
            return
        }

        Invoke-JIMPasswordQueueAction -Action 'cancel' -Id $collectedIds.ToArray() `
            -BoundParameters $PSBoundParameters -EntireQueue:$EntireQueue
    }
}
