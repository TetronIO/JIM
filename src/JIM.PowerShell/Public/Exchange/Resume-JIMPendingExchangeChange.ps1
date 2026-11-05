# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Resume-JIMPendingExchangeChange {
    <#
    .SYNOPSIS
        Retries queued Exchange changes: JIM attempts them again within seconds.

    .DESCRIPTION
        Makes every matching change that is parked, or retrying after an attempt, due at once. Its attempts, failure and
        24-hour park clock are reset, and the Exchange delivery lane is woken for it. This is what you run once the reason
        Exchange refused or failed the changes has been dealt with, such as missing permissions granted or Exchange back
        up.

        A change not yet attempted is already due and is left as it is. One being delivered is left to finish. A Held
        change waits, with its retry clock, until its Connected System manages Exchange recipients again. A Refused or
        NotMade change holds until what is declared for the account changes, and is never retried.

        Whatever it covers, this is one request: the retry is recorded as one Activity, attributed to you, and under each
        retried change's own Activity. Piped changes are collected and retried together, by their ids, whichever
        Connected Systems they are for.

        The command is named Resume- rather than Retry- because Retry is not a PowerShell approved verb.

        Exchange recipients are in development: this command answers only where the Exchange recipients feature is
        turned on.

    .PARAMETER Id
        The IDs of the queued changes to retry. Combines with the other criteria rather than replacing them, so -State
        Parked alongside piped changes means these changes, if they are still parked.

    .PARAMETER ConnectedSystemId
        Retry the changes for this Connected System.

    .PARAMETER State
        Retry only changes in this state.

    .PARAMETER FailureKind
        Retry only changes whose last attempt failed this way.

    .PARAMETER MetaverseObjectId
        Retry only this Metaverse Object's changes.

    .PARAMETER ConnectedSystemObjectId
        Retry only this account's change.

    .PARAMETER Search
        Retry only changes matching this free-text search over the Metaverse Object and Connected System names, as
        Get-JIMPendingExchangeChange -Search lists them.

    .PARAMETER EntireQueue
        Retry every parked or retrying change in the deployment. Required when nothing else narrows the request, so a
        command that names no criteria cannot act on everything by accident.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        One object, whose AffectedCount says how many changes were made due again. Zero is an answer, not an error.

    .EXAMPLE
        Resume-JIMPendingExchangeChange -ConnectedSystemId 3 -State Parked

        Retries every parked change for Connected System 3. The usual command once the reason Exchange refused them has
        been fixed.

    .EXAMPLE
        Get-JIMPendingExchangeChange -State Parked -FailureKind InsufficientPermissions -All | Resume-JIMPendingExchangeChange -Force

        Retries, in one request, every change parked for want of Exchange permissions, after they have been granted. Run
        the Get on its own first to see what that covers.

    .EXAMPLE
        Resume-JIMPendingExchangeChange -State Retrying -WhatIf

        Shows what would be retried without retrying it: the changes waiting out a backoff, which a retry attempts now
        rather than at their next scheduled attempt.

    .EXAMPLE
        $result = Resume-JIMPendingExchangeChange -ConnectedSystemId 3 -State Parked -Force
        "$($result.AffectedCount) Exchange change(s) will be attempted again."

        Reports how much the retry covered. Zero means nothing matching was parked.

    .LINK
        Get-JIMPendingExchangeChange
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.ExchangeQueueActionResponse')]
    param(
        # The only parameter a piped row binds. A row's ConnectedSystemId, bound from the pipeline, would hold the last
        # row's value by the time the request is sent and narrow it to that one system.
        [Parameter(ValueFromPipelineByPropertyName)]
        [guid[]]$Id,

        [Parameter()]
        [int]$ConnectedSystemId,

        [Parameter()]
        [ValidateSet('Retrying', 'Parked')]
        [string]$State,

        [Parameter()]
        [ValidateSet('ObjectNotFound', 'Refused', 'InsufficientPermissions', 'AuthenticationFailed', 'Unreachable')]
        [string]$FailureKind,

        [Parameter()]
        [guid]$MetaverseObjectId,

        [Parameter()]
        [guid]$ConnectedSystemObjectId,

        [Parameter()]
        [string]$Search,

        [Parameter()]
        [switch]$EntireQueue,

        [Parameter()]
        [switch]$Force
    )

    begin {
        # Collected here and sent in end: one request, so the retry is recorded as the one decision it was.
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

        # An empty pipeline is a successful no-op: "retry everything parked" on a day nothing is parked is the outcome
        # the caller wanted.
        if ($collectedIds.Count -eq 0 -and $PSCmdlet.MyInvocation.ExpectingInput) {
            Write-Verbose "Nothing reached Resume-JIMPendingExchangeChange from the pipeline; there is nothing to retry."
            return
        }

        $body = @{}
        if ($collectedIds.Count -gt 0) { $body.ids = @($collectedIds | ForEach-Object { $_.ToString() }) }
        if ($PSBoundParameters.ContainsKey('ConnectedSystemId')) { $body.connectedSystemId = $ConnectedSystemId }
        if ($State) { $body.state = $State }
        if ($FailureKind) { $body.failureKind = $FailureKind }
        if ($PSBoundParameters.ContainsKey('MetaverseObjectId')) { $body.metaverseObjectId = $MetaverseObjectId.ToString() }
        if ($PSBoundParameters.ContainsKey('ConnectedSystemObjectId')) { $body.connectedSystemObjectId = $ConnectedSystemObjectId.ToString() }
        if ($Search) { $body.searchText = $Search }

        if ($body.Count -eq 0) {
            if (-not $EntireQueue) {
                # The API refuses this too; refusing here tells the caller what to do, and no request goes out that could
                # have covered the whole deployment.
                Write-Error "This command names no Exchange changes. Supply -Id, -ConnectedSystemId, -State, -FailureKind, -MetaverseObjectId, -ConnectedSystemObjectId or -Search, or use -EntireQueue to retry the whole queue."
                return
            }

            $body.applyToAllChanges = $true
        }

        $target = if ($collectedIds.Count -eq 1) { "1 queued Exchange change" }
        elseif ($collectedIds.Count -gt 1) { "$($collectedIds.Count) queued Exchange changes" }
        elseif ($EntireQueue) { "every parked or retrying Exchange change" }
        else { "the matching queued Exchange changes" }

        if (-not (Confirm-JIMAction -Cmdlet $PSCmdlet -Target $target -Action "Attempt again" -Force:$Force)) {
            return
        }

        Invoke-JIMApi -Endpoint '/api/v1/exchange/queue/retry' -Method 'POST' -Body $body
    }
}
