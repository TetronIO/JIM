# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Invoke-JIMPasswordQueueAction {
    <#
    .SYNOPSIS
        Sends one retry or cancel over the Password Synchronisation queue.

    .DESCRIPTION
        Shared by Resume-JIMPendingPasswordChange and Stop-JIMPendingPasswordChange, which differ only in the
        endpoint they call and in how loudly they ask before doing it. Everything else (how criteria become a
        request body, and the refusal to act on a request that names nothing) is identical, and identical is
        what it needs to stay: the two are used together, usually in the same recovery, and a difference in how
        they interpret the same parameters would be found the hard way.

        One request, whatever it covers. The server records one Activity per administrator action, so a caller
        that looped per row would turn a single decision into an audit trail nobody can read.

    .PARAMETER Action
        'retry' or 'cancel'; selects the endpoint.

    .PARAMETER Id
        The queued changes to act on, collected from the pipeline or the -Id parameter. Combines with the other
        criteria rather than replacing them, so a change that has moved on since the caller listed it simply
        does not match.

    .PARAMETER BoundParameters
        The calling cmdlet's $PSBoundParameters, read for the shared criteria.

    .PARAMETER EntireQueue
        Confirms that a request naming no criteria is meant to act on the whole queue.

    .OUTPUTS
        The API's response: an object carrying AffectedCount.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('retry', 'cancel')]
        [string]$Action,

        [guid[]]$Id,

        [Parameter(Mandatory)]
        [hashtable]$BoundParameters,

        [switch]$EntireQueue
    )

    $body = @{}

    if ($Id -and $Id.Count -gt 0) { $body.ids = @($Id | ForEach-Object { $_.ToString() }) }
    if ($BoundParameters.ContainsKey('ConnectedSystemId')) { $body.connectedSystemId = $BoundParameters['ConnectedSystemId'] }
    if ($BoundParameters.ContainsKey('Status')) { $body.status = $BoundParameters['Status'] }
    if ($BoundParameters.ContainsKey('FailureReason')) { $body.failureReason = $BoundParameters['FailureReason'] }
    if ($BoundParameters.ContainsKey('MetaverseObjectId')) { $body.metaverseObjectId = $BoundParameters['MetaverseObjectId'].ToString() }
    if ($BoundParameters.ContainsKey('Search')) { $body.searchText = $BoundParameters['Search'] }

    if ($body.Count -eq 0) {
        if (-not $EntireQueue) {
            # The API refuses this too. Refusing here as well means the caller is told what to do rather than
            # reading a 400, and no request goes out that could have covered the whole deployment.
            Write-Error "This command names no password changes. Supply -Id, -ConnectedSystemId, -Status, -FailureReason, -MetaverseObjectId or -Search, or use -EntireQueue to act on the whole queue."
            return
        }

        $body.applyToAllChanges = $true
    }

    Invoke-JIMApi -Endpoint "/api/v1/password-synchronisation/queue/$Action" -Method 'POST' -Body $body
}
