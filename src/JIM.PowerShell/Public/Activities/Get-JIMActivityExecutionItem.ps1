# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMActivityExecutionItem {
    <#
    .SYNOPSIS
        Gets one Run Profile Execution Item, including its error message, or what caused it or what it caused.

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

        Item ids come from Get-JIMActivity -ExecutionItems, whose rows can be piped straight in.

    .PARAMETER Id
        The execution item to get. The rows of Get-JIMActivity -ExecutionItems can be piped straight in.

    .PARAMETER Causes
        Returns what caused the item instead, one row per cohort of causes.

    .PARAMETER Consequences
        Returns what the item went on to cause instead, one row per cohort of consequences.

    .OUTPUTS
        Without a switch, the item. With -Causes, what caused it, and with -Consequences what it went on to cause,
        in later runs as well as its own: one row per cohort (objects sharing a cause and its reason read as one row
        with a Count), with Level 1 for the nearest and one more per step.

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
        PendingExportStatus (PendingExportStatus): For outstanding Pending Exports, their status now.
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
        Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Causes |
            Format-Table Level, EdgeType, ReasonCode, Count, ObjectType, ConnectedSystem, Members

        Shows what caused an execution item, one row per cohort of causes.

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
    param(
        # No ActivityId alias: an item's id is not its Activity's, and an object carrying both must bind the item's.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'Item')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'Causes')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'Consequences')]
        [guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'Causes')]
        [switch]$Causes,

        [Parameter(Mandatory, ParameterSetName = 'Consequences')]
        [switch]$Consequences
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
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
