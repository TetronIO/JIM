# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Resume-JIMPendingPasswordChange {
    <#
    .SYNOPSIS
        Puts queued password changes back on the Password Synchronisation queue and asks JIM to deliver them now.

    .DESCRIPTION
        Makes every matching change due immediately. The Password Delivery Service is woken by the change and
        attempts them within about a second, whatever the synchronisation engine is doing. This is what you run
        once the reason a Connected System refused the passwords has been dealt with: the changes are parked
        behind that system, and nothing will attempt them again until somebody says so.

        Applies to changes JIM could still deliver, which is Pending, Parked and Cancelled. An Expired change is
        left alone: the password it carried is gone, so there is nothing to send. Retrying a change clears the
        failure recorded against it and resets its attempt count.

        Whatever it covers, this is one request and one Activity. Piping a hundred parked changes in does not
        produce a hundred audit entries; a retry over a directory that has come back is one decision, and the
        record should say so.

        The command is named Resume- rather than Retry- because Retry is not a PowerShell approved verb; a module
        exporting one warns on import. Resume is the approved verb for starting something that was suspended,
        which is what a parked or cancelled change is.

    .PARAMETER Id
        The unique identifiers of the queued changes to retry. Accepts queued changes from the pipeline.
        Combines with the other criteria rather than replacing them, so "-Status Parked" alongside piped
        changes means "these, if they are still parked": a change delivered since you listed it is not retried.

    .PARAMETER ConnectedSystemId
        Retry the changes queued for one Connected System. Accepts a Connected System from the pipeline.

    .PARAMETER Status
        Retry only changes in this state: Pending, Parked or Cancelled. Expired changes are never retried.

    .PARAMETER FailureReason
        Retry only changes whose last attempt failed this way.

    .PARAMETER MetaverseObjectId
        Retry only one identity's queued changes.

    .PARAMETER Search
        Retry only changes matching this free-text search over the identity and Connected System names.

    .PARAMETER EntireQueue
        Retry every queued password change in the deployment. Required when nothing else narrows the request,
        so a command that names no criteria cannot act on everything by accident.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        PSCustomObject with an AffectedCount property: how many changes were made due again.

    .EXAMPLE
        Resume-JIMPendingPasswordChange -ConnectedSystemId 3

        Retries every queued password change for Connected System 3. The usual command after a directory that
        was refusing passwords has been fixed.

    .EXAMPLE
        Get-JIMPendingPasswordChange -Status Parked | Resume-JIMPendingPasswordChange -Force

        Retries every parked change, in a single request. Run the Get on its own first to see what that covers.

    .EXAMPLE
        Resume-JIMPendingPasswordChange -Status Parked -FailureReason Transient -WhatIf

        Shows what would be retried without retrying it: the changes that failed for a reason that may well have
        gone away on its own.

    .EXAMPLE
        $result = Resume-JIMPendingPasswordChange -ConnectedSystemId 3 -Force
        "$($result.AffectedCount) password change(s) will be attempted again."

        Reports how much the retry covered. Zero is a valid answer, not an error: it means nothing was owed.

    .LINK
        Get-JIMPendingPasswordChange
        Stop-JIMPendingPasswordChange
        Sync-JIMMetaverseObjectPassword
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
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
        # Collected here and acted on in end, deliberately. A per-row request would be one Activity per row, and
        # the server records an Activity per administrator action precisely so a bulk retry reads as the one
        # decision it was.
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

        # An empty pipeline is a successful no-op, not a failure: "retry everything parked" on a day when
        # nothing is parked is exactly the outcome the caller wanted.
        if ($collectedIds.Count -eq 0 -and $PSCmdlet.MyInvocation.ExpectingInput -and -not $PSBoundParameters.ContainsKey('ConnectedSystemId')) {
            Write-Verbose "Nothing reached Resume-JIMPendingPasswordChange from the pipeline; there is nothing to retry."
            return
        }

        $target = if ($collectedIds.Count -gt 0) {
            if ($collectedIds.Count -eq 1) { "1 queued password change" } else { "$($collectedIds.Count) queued password changes" }
        }
        elseif ($EntireQueue) { "every queued password change" }
        else { "the matching queued password changes" }

        if (-not $Force -and -not $PSCmdlet.ShouldProcess($target, "Attempt delivery again")) {
            return
        }

        Invoke-JIMPasswordQueueAction -Action 'retry' -Id $collectedIds.ToArray() `
            -BoundParameters $PSBoundParameters -EntireQueue:$EntireQueue
    }
}
