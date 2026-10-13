# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMActivityExecutionItem {
    <#
    .SYNOPSIS
        Gets one Run Profile Execution Item, including its error message, or what caused it or what it caused; or
        every execution item about one object, each with its nearest cause.

    .DESCRIPTION
        Retrieves one execution item of a Run Profile Activity: what happened to one object and, when it failed,
        why. The list from Get-JIMActivity -ExecutionItems names each failed item's error type; this returns the
        error message and stack trace too, with the snapshots taken when the item was recorded (the object's
        external ID, display name and type) and the ids of the Connected System Object, Metaverse Object or
        Pending Export it is about.

        With -Causes, returns what caused the item instead, walked back through the causes JIM recorded, and with
        -Consequences what the item went on to cause, in later runs as well as its own. Both are flattened to one
        row per cohort (objects sharing a cause and its reason read as one row with a Count), with Level 1 for the
        nearest and one more per step. A consequences row says in Ending where its branch stopped, and carries the
        CohortKey that Get-JIMCausalCohortMember pages its members with.

        With -ConnectedSystemId and -ConnectedSystemObjectId, or with -MetaverseObjectId, returns the object's causal
        timeline instead: every execution item about it, newest first, each with its nearest recorded cause, as the
        Causality Explorer lists them. A deleted object's timeline is still read, through the records its deletion
        left.

        Item ids come from Get-JIMActivity -ExecutionItems or from a timeline, whose rows can be piped straight in.

    .PARAMETER Id
        The execution item to get. The rows of Get-JIMActivity -ExecutionItems can be piped straight in.

    .PARAMETER Causes
        Return what caused the item instead, one row per cohort of causes.

    .PARAMETER Consequences
        Return what the item went on to cause instead, one row per cohort of consequences.

    .PARAMETER ConnectedSystemId
        The Connected System holding the Connected System Object whose timeline to return.

    .PARAMETER ConnectedSystemObjectId
        The Connected System Object whose timeline to return. Get-JIMConnectedSystemObject returns it as Id.

    .PARAMETER MetaverseObjectId
        The Metaverse Object whose timeline to return. Get-JIMMetaverseObject returns it as Id.

    .PARAMETER Page
        The page of the timeline to return, newest items first.

    .PARAMETER PageSize
        How many items each page of the timeline holds.

    .PARAMETER All
        Return the whole timeline, fetching every page. Stops after 1000 pages with a warning unless -Force is given.

    .PARAMETER Force
        Fetch past the 1000-page limit of -All.

    .OUTPUTS
        Without a switch, the item. With -Causes, what caused it, and with -Consequences what it went on to cause,
        in later runs as well as its own: one row per cohort (objects sharing a cause and its reason read as one row
        with a Count), with Level 1 for the nearest and one more per step. With an object's id, the object's
        timeline, one row per execution item, newest first; where the deployment records no Metaverse Object
        changes, a Metaverse Object's timeline holds only what its joined Connected System Objects recorded, and a
        warning says so.

        JIM.CauseCohort
        ExecutionItemId (guid): The execution item the walk started from.
        Level (int): How many steps back from the item: 1 for its direct causes.
        EdgeType (CausalEdgeType): The recorded cause; empty for a step to the import that fed a synchronisation, or
            to the Metaverse Object's creation.
        SourceImportChangeType (ObjectChangeType): For the step to the import that fed a synchronisation, what that
            import did: Added, Updated or Deleted.
        MetaverseChangeType (ObjectChangeType): For the step to the Metaverse Object's creation: Projected, Joined
            or Created.
        ReasonCode (CausalReasonCode): Why the cause produced its effect; NotSet where no reason was recorded.
        Count (int): How many causes the cohort holds.
        ObjectType (string): The causes' object type, plural where there are several.
        Attribute (string): The reference attribute the causes acted through, where one applies.
        ConnectedSystem (string): The Connected System the causes are attributed to, as named when recorded.
        SyncRule (string): The Synchronisation Rule responsible, as named when recorded.
        Members (string[]): The causes' names, as recorded.
        Resolution (string): Why the walk stopped at these causes, for example CauseNotRetained; empty where each
            carries on to the next level.

        JIM.ConsequenceCohort
        ExecutionItemId (guid): The execution item the walk started from.
        Level (int): How many steps on from the item: 1 for what it directly caused. A Level 0 row gives the item's
            own ending where it caused nothing.
        HopKind (CausalConsequenceHopKind): How the consequences were reached: Edge (a recorded cause), Confirmation
            (the import confirming an export), NextSynchronisation (the synchronisation after an import),
            OutstandingPendingExport or ScheduledDeletion.
        EdgeType (CausalEdgeType): The recorded cause, for an Edge or Confirmation hop.
        ReasonCode (CausalReasonCode): Why the cause produced these consequences; NotSet for any other hop.
        Count (int): How many consequences the cohort holds, counted exactly.
        ObjectType (string): The consequences' object type, plural where there are several.
        Attribute (string): The reference attribute a removal acted through, where one applies.
        ConnectedSystem (string): The Connected System the consequences are attributed to.
        SyncRule (string): The Synchronisation Rule the cause names, where it names one.
        OutcomeSummary (string): The outcomes the consequences share.
        PendingExportStatus (PendingExportStatus): For outstanding Pending Exports, their live status.
        Ending (string): Where the branch stops: the cohort's own ending (Confirmed, WaitingForExport,
            WaitingForRetry, NeedsAttention, WaitingForConfirmingImport or DeletionScheduled), else how many
            consequences led nowhere further and why, for example "NoFurtherConsequences (2)", else FurtherThanShown
            where the walk reached its bound. Empty where every consequence carries on.
        FirstOccurred (datetime): When the earliest consequence happened, in UTC.
        LastOccurred (datetime): When the latest consequence happened, in UTC.
        DueAt (datetime): When the soonest outstanding work is due, in UTC: a retry, or a deletion becoming
            eligible.
        Members (string[]): The first consequences' names; Get-JIMCausalCohortMember pages through them all.
        CohortKey (string): The cohort's key, which Get-JIMCausalCohortMember pages its members with.

    .EXAMPLE
        Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901"

        Gets one execution item, with its error message if it failed.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
            Where-Object { $_.ErrorType -and $_.ErrorType -ne 'NotSet' } |
            Get-JIMActivityExecutionItem |
            Select-Object DisplayNameSnapshot, ErrorType, ErrorMessage

        Lists why each object in a Run Profile execution failed.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
            Get-JIMActivityExecutionItem |
            Where-Object { $_.UnresolvedReferenceCount -gt 0 } |
            Select-Object DisplayNameSnapshot, UnresolvedReferenceCount, ErrorType

        Lists every object an import left with unresolved references, including those whose item carries a
        different error.

    .EXAMPLE
        (Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901").MatchCandidates |
            Select-Object DisplayNameSnapshot, MatchedValue, Exists,
                @{ Name = 'JoinedTo'; Expression = { $_.JoinedObjects.ConnectedSystemName -join ', ' } }

        Lists the identities an ambiguous match found, the value each matched on, and where each is connected.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
            Where-Object ObjectChangeType -eq 'Joined' |
            Get-JIMActivityExecutionItem |
            Select-Object DisplayNameSnapshot, JoinMatchedValue, JoinObjectMatchingRuleDescriptionSnapshot

        Lists each object a synchronisation joined, with the value it matched on and the Object Matching Rule that
        made the join.

    .EXAMPLE
        Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Causes |
            Format-Table Level, EdgeType, ReasonCode, Count, ObjectType, ConnectedSystem, Members

        Shows what caused an execution item, one row per cohort of causes.

    .EXAMPLE
        Get-JIMActivityExecutionItem -ConnectedSystemId 3 -ConnectedSystemObjectId "c3d4e5f6-a7b8-9012-cdef-123456789012" |
            Format-Table Executed, TargetName, ObjectChangeType, Because

        Lists what happened to a Connected System Object, newest first, and why.

    .EXAMPLE
        Get-JIMActivityExecutionItem -MetaverseObjectId "d4e5f6a7-b8c9-0123-def0-234567890123" -All |
            Where-Object ObjectChangeType -eq 'Deprovisioned' |
            Get-JIMActivityExecutionItem -Causes

        Finds each time a Metaverse Object was deprovisioned somewhere, and walks back through what caused it.

    .EXAMPLE
        Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Consequences |
            Where-Object Ending -like 'Waiting*' |
            Format-Table Level, HopKind, Count, ObjectType, ConnectedSystem, Ending, DueAt

        Shows which of an execution item's consequences are still waiting, and on what.

    .LINK
        Get-JIMActivity
        Get-JIMActivityStats
        Get-JIMCausalCohortMember
    #>
    [CmdletBinding(DefaultParameterSetName = 'Item')]
    [OutputType('JIM.ActivityRunProfileExecutionItemDetailDto', ParameterSetName = 'Item')]
    [OutputType('JIM.CauseCohort', ParameterSetName = 'Causes')]
    [OutputType('JIM.ConsequenceCohort', ParameterSetName = 'Consequences')]
    [OutputType('JIM.CausalTimelineRowDto', ParameterSetName = 'ConnectedSystemObjectTimeline')]
    [OutputType('JIM.CausalTimelineRowDto', ParameterSetName = 'ConnectedSystemObjectTimelineAll')]
    [OutputType('JIM.CausalTimelineRowDto', ParameterSetName = 'MetaverseObjectTimeline')]
    [OutputType('JIM.CausalTimelineRowDto', ParameterSetName = 'MetaverseObjectTimelineAll')]
    param(
        # No ActivityId alias: an item's id is not its Activity's, and an object carrying both must bind the item's.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'Item')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'Causes')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'Consequences')]
        [guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'Causes')]
        [switch]$Causes,

        [Parameter(Mandatory, ParameterSetName = 'Consequences')]
        [switch]$Consequences,

        # The timeline sets bind nothing from the pipeline. A piped Connected System Object or Metaverse Object
        # carries its own id as Id, which the item sets above already bind by default, so piping one in could only
        # select a timeline through a switch that existed for nothing else; the object's ids are passed explicitly.
        [Parameter(Mandatory, ParameterSetName = 'ConnectedSystemObjectTimeline')]
        [Parameter(Mandatory, ParameterSetName = 'ConnectedSystemObjectTimelineAll')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ConnectedSystemObjectTimeline')]
        [Parameter(Mandatory, ParameterSetName = 'ConnectedSystemObjectTimelineAll')]
        [guid]$ConnectedSystemObjectId,

        [Parameter(Mandatory, ParameterSetName = 'MetaverseObjectTimeline')]
        [Parameter(Mandatory, ParameterSetName = 'MetaverseObjectTimelineAll')]
        [guid]$MetaverseObjectId,

        [Parameter(ParameterSetName = 'ConnectedSystemObjectTimeline')]
        [Parameter(ParameterSetName = 'MetaverseObjectTimeline')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'ConnectedSystemObjectTimeline')]
        [Parameter(ParameterSetName = 'ConnectedSystemObjectTimelineAll')]
        [Parameter(ParameterSetName = 'MetaverseObjectTimeline')]
        [Parameter(ParameterSetName = 'MetaverseObjectTimelineAll')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'ConnectedSystemObjectTimelineAll')]
        [Parameter(Mandatory, ParameterSetName = 'MetaverseObjectTimelineAll')]
        [switch]$All,

        [Parameter(ParameterSetName = 'ConnectedSystemObjectTimelineAll')]
        [Parameter(ParameterSetName = 'MetaverseObjectTimelineAll')]
        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -like '*Timeline*') {
            $timelineEndpoint = if ($PSCmdlet.ParameterSetName -like 'ConnectedSystemObject*') {
                "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$ConnectedSystemObjectId/causal-timeline"
            }
            else {
                "/api/v1/metaverse/objects/$MetaverseObjectId/causal-timeline"
            }
            Write-Verbose "Getting causal timeline: $timelineEndpoint"

            # Said once however many pages are read: a hashtable, so the page request below can mark it from its own
            # scope.
            $notice = @{ Given = $false }
            $pageRequest = {
                param($p)
                $response = Invoke-JIMApi -Endpoint "$($timelineEndpoint)?page=$p&pageSize=$PageSize"
                if ($response.metaverseObjectChangesNotRecorded -and -not $notice.Given) {
                    Write-Warning "This deployment does not record Metaverse Object changes, so the timeline holds only what the object's joined Connected System Objects recorded."
                    $notice.Given = $true
                }
                $response
            }

            if ($All) {
                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMActivityExecutionItem' -PageSize $PageSize -Force:$Force `
                    -ItemNoun 'execution items'
            }
            else {
                $response = & $pageRequest $Page
                foreach ($row in $response.items) {
                    $row
                }
            }
            return
        }

        if ($Causes) {
            Write-Verbose "Getting what caused execution item: $Id"
            $chain = Invoke-JIMApi -Endpoint "/api/v1/activities/items/$Id/causes"
            if ($chain) {
                ConvertTo-JIMCausalCauseRows -Cohorts $chain.Cohorts -ExecutionItemId $Id
            }
            return
        }

        if ($Consequences) {
            Write-Verbose "Getting what execution item $Id went on to cause"
            $walk = Invoke-JIMApi -Endpoint "/api/v1/activities/items/$Id/consequences"
            if ($walk) {
                ConvertTo-JIMCausalConsequenceRows -Consequences $walk
            }
            return
        }

        Write-Verbose "Getting execution item with ID: $Id"
        Invoke-JIMApi -Endpoint "/api/v1/activities/items/$Id"
    }
}
