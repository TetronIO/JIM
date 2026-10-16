# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPendingPasswordChange {
    <#
    .SYNOPSIS
        Gets the Password Synchronisation queue: password changes waiting to be delivered to Connected Systems.

    .DESCRIPTION
        Lists what Set-JIMMetaverseObjectPassword put on the queue and what has happened to it since. One row
        per identity per Connected System, naming both, so a list reads as people and systems rather than
        identifiers.

        A change is in one of five states:

        - Pending: JIM still intends to deliver it. Read the Due and Held properties alongside this, which say
          whether the Password Delivery Service would attempt it now: a Pending change may be waiting out a
          retry backoff, or be Held because Password Synchronisation is switched off on its Connected System. A
          held change is never due; switching the system on delivers everything that accumulated for it.
        - Delivering: the Password Delivery Service has claimed it and is writing it to the Connected System
          right now. Momentary; it is gone once the target has the password, or back to Pending or Parked if
          the target refused.
        - Parked: the target refused it, or it ran out of attempts. JIM has stopped trying and it waits on a
          person. FailureReason and TargetMessage say why, in the target's own words.
        - Expired: it outlived its Connected System's time to live. The password it carried is gone and nothing
          can deliver it now.
        - Cancelled: an administrator stopped it. CancelledAt and CancelledByName record who and when.

        Every change also carries an Origin: Explicit (an administrator's named set), Propagated (JIM carried
        it to every configured system after the person's own password change), or Provisioned (the first
        password for an account an export has just created). A Provisioned change carries SyncRuleId, the
        Synchronisation Rule its password is generated from at each delivery attempt.

        An Explicit or Propagated change can also be a Connected System Object's first password: it was already
        waiting when JIM provisioned that object, or it replaced the object's Initial Password before it was
        delivered. ProvisionedConnectedSystemObjectId names the object, and SyncRuleId the Synchronisation Rule
        whose Initial Password settings for enabling the object and for whether its password never expires its
        delivery applies; the change keeps its own password and expiry. Cancelling such a change leaves the object
        without a password from JIM. Both are $null for a change that is no object's first password.

        No password is returned, in any form. The queued value is encrypted in the database and has no
        representation on this or any other surface.

    .PARAMETER ConnectedSystemId
        Only changes queued for this Connected System.

    .PARAMETER Status
        Only changes in this state.

    .PARAMETER FailureReason
        Only changes whose last attempt failed this way. A change that has never been attempted has no reason.

    .PARAMETER MetaverseObjectId
        Only this Metaverse Object's queued changes.

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
        One object per queued change, one entry per Metaverse Object per Connected System, or with -Summary one
        object of counts. No password is returned, in any form.

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
        Get-JIMPendingPasswordChange -Status Pending | Where-Object { -not $_.Due -and -not $_.Held }

        Lists changes waiting out a retry backoff, as opposed to those the Password Delivery Service is about
        to attempt or those held behind a Connected System that is switched off.

    .EXAMPLE
        Get-JIMPendingPasswordChange -Status Pending -All | Where-Object Held |
            Group-Object ConnectedSystemName | Select-Object Name, Count

        Lists the Connected Systems holding password changes because Password Synchronisation is switched off on
        them, and how many each is holding. Worth running before a change window closes: every one of these
        expires eventually, and switching the system back on is what delivers them.

    .EXAMPLE
        Get-JIMPendingPasswordChange -All |
            Where-Object { $_.ProvisionedConnectedSystemObjectId -and $_.Origin -ne 'Provisioned' }

        Lists the password changes that are also a Connected System Object's first password, which is worth
        knowing before cancelling any of them.

    .LINK
        Resume-JIMPendingPasswordChange
        Stop-JIMPendingPasswordChange
        Set-JIMMetaverseObjectPassword
        Set-JIMConnectedSystemPasswordSynchronisation
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.PendingPasswordChangeResponse', ParameterSetName = 'List')]
    [OutputType('JIM.PendingPasswordChangeResponse', ParameterSetName = 'ListAll')]
    [OutputType('JIM.PasswordQueueSummary', ParameterSetName = 'Summary')]
    param(
        [Parameter(ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(ParameterSetName = 'ListAll', ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('Pending', 'Delivering', 'Parked', 'Expired', 'Cancelled')]
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
