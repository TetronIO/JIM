# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPendingPasswordChange {
    <#
    .SYNOPSIS
        Gets the Password Synchronisation queue: password changes waiting to be delivered to Connected Systems.

    .DESCRIPTION
        Lists what Sync-JIMMetaverseObjectPassword put on the queue and what has happened to it since. One row
        per identity per Connected System, naming both, so a list reads as people and systems rather than
        identifiers.

        A change is in one of four states:

        - Pending: JIM still intends to deliver it. Read the Due property alongside this, which says whether the
          next delivery pass would attempt it: a Pending change may be waiting out a retry backoff.
        - Parked: the target refused it, or it ran out of attempts. JIM has stopped trying and it waits on a
          person. FailureReason and TargetMessage say why, in the target's own words.
        - Expired: it outlived its Connected System's time to live. The password it carried is gone and nothing
          can deliver it now.
        - Cancelled: an administrator stopped it. CancelledAt and CancelledByName record who and when.

        No password is returned, in any form. The queued value is encrypted in the database and has no
        representation on this or any other surface.

    .PARAMETER ConnectedSystemId
        Restrict to one Connected System. Accepts a Connected System from the pipeline.

    .PARAMETER Status
        Restrict to one state: Pending, Parked, Expired or Cancelled.

    .PARAMETER FailureReason
        Restrict to changes whose last attempt failed this way. Only meaningful for changes that have been
        attempted; one that has never been tried has no reason.

    .PARAMETER MetaverseObjectId
        Restrict to one identity's queued changes.

    .PARAMETER Search
        Free-text search over the identity and Connected System names.

    .PARAMETER SortBy
        The column to sort by: queued (the default), identity, system, status, attempts, nextAttempt or expires.

    .PARAMETER SortDirection
        asc (the default) or desc.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page (1-100). Defaults to 50.

    .PARAMETER All
        Retrieves every page of results. Fetches at most 1000 pages before stopping with a warning; use -Force
        to fetch beyond the cap.

    .PARAMETER Force
        Overrides the -All page ceiling. Only valid with -All.

    .PARAMETER Summary
        Returns the queue's counts by state instead of its rows.

    .OUTPUTS
        PSCustomObject per queued password change, or a single summary object with -Summary.

    .EXAMPLE
        Get-JIMPendingPasswordChange -Summary

        Reports how much is waiting, how much is due now, and how much is parked or expired. The cheapest way
        to find out whether Password Synchronisation needs attention.

    .EXAMPLE
        Get-JIMPendingPasswordChange -Status Parked

        Lists the changes JIM has given up on, which are the ones a person has to do something about.

    .EXAMPLE
        Get-JIMPendingPasswordChange -Status Parked |
            Group-Object ConnectedSystemName |
            Select-Object Name, Count

        Shows which Connected Systems the parked changes are piling up behind, which is usually one system
        rather than many.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Corporate AD" | Get-JIMPendingPasswordChange -Status Parked -All

        Lists every parked change for one Connected System, following pagination.

    .EXAMPLE
        Get-JIMPendingPasswordChange -Status Pending | Where-Object { -not $_.Due }

        Lists changes waiting out a retry backoff, as opposed to those the next delivery pass will attempt.

    .LINK
        Resume-JIMPendingPasswordChange
        Stop-JIMPendingPasswordChange
        Sync-JIMMetaverseObjectPassword
        Set-JIMConnectedSystemPasswordSynchronisation
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(ParameterSetName = 'ListAll', ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('Pending', 'Parked', 'Expired', 'Cancelled')]
        [string]$Status,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('None', 'Transient', 'ConfigurationFault', 'PolicyRejection', 'TargetObjectNotFound', 'UnsupportedOperation')]
        [string]$FailureReason,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [guid]$MetaverseObjectId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('queued', 'identity', 'system', 'status', 'attempts', 'nextAttempt', 'expires')]
        [string]$SortBy,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('asc', 'desc')]
        [string]$SortDirection,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'ListAll')]
        [switch]$All,

        [Parameter(ParameterSetName = 'ListAll')]
        [switch]$Force,

        [Parameter(Mandatory, ParameterSetName = 'Summary')]
        [switch]$Summary
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'Summary') {
            Write-Verbose "Getting the Password Synchronisation queue summary"
            Invoke-JIMApi -Endpoint '/api/v1/password-synchronisation/queue/summary'
            return
        }

        # Built once and reused across pages, so an auto-paginating fetch cannot drift from the first page's
        # filters partway through.
        $criteria = @()
        if ($PSBoundParameters.ContainsKey('ConnectedSystemId')) { $criteria += "connectedSystemId=$ConnectedSystemId" }
        if ($Status) { $criteria += "status=$Status" }
        if ($FailureReason) { $criteria += "failureReason=$FailureReason" }
        if ($PSBoundParameters.ContainsKey('MetaverseObjectId')) { $criteria += "metaverseObjectId=$MetaverseObjectId" }
        if ($Search) { $criteria += "search=$([System.Uri]::EscapeDataString($Search))" }
        if ($SortBy) { $criteria += "sortBy=$SortBy" }
        if ($SortDirection) { $criteria += "sortDirection=$SortDirection" }
        $criteriaSuffix = if ($criteria.Count -gt 0) { '&' + ($criteria -join '&') } else { '' }

        if ($All) {
            Write-Verbose "Getting every queued password change"
            $pageRequest = {
                param($p)
                Invoke-JIMApi -Endpoint "/api/v1/password-synchronisation/queue?page=$p&pageSize=$PageSize$criteriaSuffix"
            }

            Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMPendingPasswordChange' -PageSize $PageSize -Force:$Force `
                -ItemNoun 'queued password changes' -NarrowHint 'narrow the query with -ConnectedSystemId, -Status or -Search'
            return
        }

        Write-Verbose "Getting queued password changes (Page: $Page, PageSize: $PageSize)"
        $response = Invoke-JIMApi -Endpoint "/api/v1/password-synchronisation/queue?page=$Page&pageSize=$PageSize$criteriaSuffix"
        foreach ($item in $response.items) {
            $item
        }
    }
}
