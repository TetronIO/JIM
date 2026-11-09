# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPendingExchangeChange {
    <#
    .SYNOPSIS
        Gets the Exchange queue: the recipient changes JIM has queued for Exchange, and where each one stands.

    .DESCRIPTION
        Lists the changes exports have queued for Exchange: one row per account, naming its Connected System and
        Metaverse Object. A change leaves the queue once Exchange has it; its outcome stays on its Activity.

        A change stands in one of six states:

        - Queued: waiting for its first attempt. The Exchange delivery lane picks it up within seconds.
        - Delivering: Exchange is applying it now.
        - Retrying: an attempt failed in a way that may clear by itself (Exchange unreachable, the account not yet
          visible to Exchange). NextRetryAt says when the next attempt falls due.
        - Parked: Exchange refused it, JIM lacks the permissions to make it, or 24 hours of retries failed. JIM has
          stopped trying; FailureMessage gives Exchange's own words. Retry it with Resume-JIMPendingExchangeChange once
          the cause is fixed.
        - Refused: JIM refused the declared state before asking Exchange, for example a recipient type that does not
          fit the object. It holds until what is declared for the account changes.
        - NotMade: it would change an existing recipient's type, which JIM does not do. It holds until what
          is declared for the account changes.

        An Exchange change cannot be cancelled. To stop or correct one, change what is declared for the account (its
        source data or the Attribute Flow); the newer state replaces the queued change.

    .PARAMETER ConnectedSystemId
        Only changes for this Connected System.

    .PARAMETER State
        Only changes in this state. Held changes wait, unattempted, because their Connected System does not manage
        Exchange recipients now; they are delivered once it does again.

    .PARAMETER FailureKind
        Only changes whose last attempt failed this way.

    .PARAMETER MetaverseObjectId
        Only this Metaverse Object's changes.

    .PARAMETER ConnectedSystemObjectId
        Only this account's change. The queue holds at most one per account.

    .PARAMETER Search
        Free-text search over the Metaverse Object and Connected System names.

    .PARAMETER SortBy
        The column to sort by; queued when omitted.

    .PARAMETER SortDirection
        The sort direction; ascending when omitted.

    .PARAMETER Page
        The page of changes to return.

    .PARAMETER PageSize
        How many changes to return per page.

    .PARAMETER All
        Returns every page. Stops after 1,000 pages with a warning unless -Force is given.

    .PARAMETER Force
        Fetches beyond the -All page ceiling.

    .PARAMETER Summary
        Returns the queue's counts by state instead of its changes.

    .OUTPUTS
        One object per queued change, or with -Summary one object of counts.

    .EXAMPLE
        Get-JIMPendingExchangeChange -Summary

        Reports how many changes stand in each state, and how many wait on a person. The quickest way to find out
        whether Exchange needs attention.

    .EXAMPLE
        Get-JIMPendingExchangeChange -State Parked | Select-Object MetaverseObjectDisplayName, ConnectedSystemName, FailureKind, FailureMessage

        Lists the changes JIM has stopped trying, with Exchange's reason for each: the ones a person has to deal with.

    .EXAMPLE
        Get-JIMPendingExchangeChange -State Parked -All | Group-Object FailureKind | Select-Object Name, Count

        Shows what is behind the parked changes, which is usually one cause (missing permissions, say) rather than many.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Panoply AD" | Get-JIMPendingExchangeChange -All

        Lists every queued change for one Connected System, following pagination.

    .LINK
        Resume-JIMPendingExchangeChange
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.PendingExchangeChangeResponse', ParameterSetName = 'List')]
    [OutputType('JIM.PendingExchangeChangeResponse', ParameterSetName = 'ListAll')]
    [OutputType('JIM.ExchangeQueueSummary', ParameterSetName = 'Summary')]
    param(
        # Aliased so a Connected System piped in binds by its Id. The noun here is the queued change, not the Connected
        # System, so -ConnectedSystemId is spelt out in every example.
        [Parameter(ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(ParameterSetName = 'ListAll', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('Queued', 'Held', 'Delivering', 'Retrying', 'Parked', 'Refused', 'NotMade')]
        [string]$State,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('ObjectNotFound', 'Refused', 'InsufficientPermissions', 'AuthenticationFailed', 'Unreachable')]
        [string]$FailureKind,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [guid]$MetaverseObjectId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [guid]$ConnectedSystemObjectId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('queued', 'identity', 'system', 'attempts', 'nextAttempt', 'lastAttempt')]
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
            Write-Verbose "Getting the Exchange queue summary"
            Invoke-JIMApi -Endpoint '/api/v1/exchange/queue/summary'
            return
        }

        # Built once and reused across pages, so an auto-paginating fetch cannot drift from the first page's filters.
        $criteria = @()
        if ($PSBoundParameters.ContainsKey('ConnectedSystemId')) { $criteria += "connectedSystemId=$ConnectedSystemId" }
        if ($State) { $criteria += "state=$State" }
        if ($FailureKind) { $criteria += "failureKind=$FailureKind" }
        if ($PSBoundParameters.ContainsKey('MetaverseObjectId')) { $criteria += "metaverseObjectId=$MetaverseObjectId" }
        if ($PSBoundParameters.ContainsKey('ConnectedSystemObjectId')) { $criteria += "connectedSystemObjectId=$ConnectedSystemObjectId" }
        if ($Search) { $criteria += "search=$([System.Uri]::EscapeDataString($Search))" }
        if ($SortBy) { $criteria += "sortBy=$SortBy" }
        if ($SortDirection) { $criteria += "sortDirection=$SortDirection" }
        $criteriaSuffix = if ($criteria.Count -gt 0) { '&' + ($criteria -join '&') } else { '' }

        if ($All) {
            Write-Verbose "Getting every queued Exchange change"
            $pageRequest = {
                param($p)
                Invoke-JIMApi -Endpoint "/api/v1/exchange/queue?page=$p&pageSize=$PageSize$criteriaSuffix"
            }

            Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMPendingExchangeChange' -PageSize $PageSize -Force:$Force `
                -ItemNoun 'queued Exchange changes' -NarrowHint 'narrow the query with -ConnectedSystemId, -State or -Search'
            return
        }

        Write-Verbose "Getting queued Exchange changes (Page: $Page, PageSize: $PageSize)"
        $response = Invoke-JIMApi -Endpoint "/api/v1/exchange/queue?page=$Page&pageSize=$PageSize$criteriaSuffix"
        foreach ($item in $response.items) {
            $item
        }
    }
}
