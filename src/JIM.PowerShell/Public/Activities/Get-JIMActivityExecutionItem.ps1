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
        The unique identifier (GUID) of the execution item.

    .PARAMETER Causes
        Return what caused the item, one row per cohort of causes.

    .PARAMETER Consequences
        Return what the item went on to cause, one row per cohort of consequences.

    .OUTPUTS
        Without a switch: PSCustomObject with Id, ActivityId, ObjectChangeType, NoChangeReason,
        ConnectedSystemObjectId, MetaverseObjectId, PendingExportId, ExternalIdSnapshot, DisplayNameSnapshot,
        ObjectTypeSnapshot, ErrorType, ErrorMessage, ErrorStackTrace, AttributeFlowCount, UnresolvedReferenceCount,
        OutcomeSummary, MatchCandidates, JoinObjectMatchingRuleId, JoinObjectMatchingRuleDescriptionSnapshot and
        JoinMatchedValue.

        The three Join properties are set when the object was joined to an existing Metaverse Object by matching:
        the Object Matching Rule that found it, what the rule compared at the time (for example
        'Employee ID equals employeeId'), and the value the join matched on. They are empty for any other item.

        UnresolvedReferenceCount is how many of the object's reference values the import could not resolve to a
        Connected System Object. It is set whatever ErrorType the item carries and under every Unresolved Reference
        Handling mode, so filter on it rather than on ErrorType to find every object an import left with
        unresolved references.

        MatchCandidates is empty unless the item is an AmbiguousMatch (the Metaverse Objects a join found) or an
        AmbiguousProvisioningMatch (the Connected System Objects matching found before provisioning). Then it lists
        up to 10 of the objects the match found, ordered by display name, each with CandidateKind, CandidateId, DisplayNameSnapshot,
        TypeNameSnapshot, ConnectedSystemId, ConnectedSystemNameSnapshot, MatchedValue, ObjectMatchingRuleId,
        ObjectMatchingRuleDescriptionSnapshot, Ordinal, CandidateTotal (how many the match found in all), Exists,
        and JoinedObjects: the Connected System Objects joined to a Metaverse Object candidate now, each with
        ConnectedSystemObjectId, ConnectedSystemId, ConnectedSystemName, TypeName, DisplayName and ExternalId.

        With -Causes: PSCustomObject with ExecutionItemId, Level, EdgeType, SourceImportChangeType,
        MetaverseChangeType, ReasonCode, Count, ObjectType, Attribute, ConnectedSystem, SyncRule, Members and
        Resolution.

        With -Consequences: PSCustomObject with ExecutionItemId, Level, HopKind, EdgeType, ReasonCode, Count,
        ObjectType, Attribute, ConnectedSystem, SyncRule, OutcomeSummary, PendingExportStatus, Ending,
        FirstOccurred, LastOccurred, DueAt, Members and CohortKey. A Level 0 row gives the item's own ending when it
        caused nothing.

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
    [OutputType([PSCustomObject])]
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
