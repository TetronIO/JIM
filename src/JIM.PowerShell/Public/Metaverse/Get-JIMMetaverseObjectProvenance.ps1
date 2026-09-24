# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMetaverseObjectProvenance {
    <#
    .SYNOPSIS
        Gets the value provenance of a Metaverse Object, or of one of its attributes.

    .DESCRIPTION
        Shows where a Metaverse Object's attribute values came from: which Connected System and
        Synchronisation Rule contributed each one, or that no contributor is recorded (#399).

        With just -Id, returns a summary: one entry per attribute holding at least one value,
        with its distinct origins ordered by value count descending.

        With -AttributeName or -AttributeId, returns everything the attribute inspector shows for
        that one attribute: the current value(s) and their origin, the Metaverse Object's joined
        Connected System Object the value came from, the change that most recently set the value,
        every Synchronisation Rule mapping that could contribute to the attribute (in priority
        order, each with the value it would currently supply and its standing against the value in
        use), and the attribute's change history.

    .PARAMETER Id
        The unique identifier (GUID) of the Metaverse Object.

    .PARAMETER AttributeName
        The name of a Metaverse Attribute to get detailed provenance for. Resolved against the
        Metaverse Object's own attributes (an exact, case-insensitive match); an attribute the
        object holds no value for, or that does not exist, returns a clear error rather than a
        404. Cannot be used with -AttributeId.

    .PARAMETER AttributeId
        The unique identifier of a Metaverse Attribute to get detailed provenance for. Cannot be
        used with -AttributeName.

    .OUTPUTS
        With just -Id: a PSCustomObject with MetaverseObjectId and Attributes (each with
        AttributeId, AttributeName and Origins, an array of {Kind, ConnectedSystemId,
        ConnectedSystemName, SyncRuleId, SyncRuleName, SyncRuleDeleted, AssertsNoValue}).

        With -AttributeName or -AttributeId: a PSCustomObject with MetaverseObjectId,
        MetaverseObjectTypeId, AttributeId, AttributeName, AttributeType, AttributePlurality,
        CurrentValues (each with DisplayValue, ReferenceMetaverseObjectId, ReferenceTypeName,
        Origin), CurrentValueTotalCount, ContributingConnectedSystemObject, LastSet (the Activity
        that set the current value), Sources (each mapping's Rank, SyncRuleId, SyncRuleName,
        ConnectedSystemId, ConnectedSystemName, IsExpression, Expression, State, CandidateValues,
        Note), History (newest first) and HistoryTruncated.

    .EXAMPLE
        Get-JIMMetaverseObjectProvenance -Id "12345678-1234-1234-1234-123456789abc"

        Gets the origin of every attribute holding a value on the Metaverse Object.

    .EXAMPLE
        Get-JIMMetaverseObjectProvenance -Id "12345678-1234-1234-1234-123456789abc" -AttributeName Department

        Gets full provenance for the Department attribute: its current value, the source that won,
        every other Synchronisation Rule that could contribute to it, and its history.

    .EXAMPLE
        (Get-JIMMetaverseObjectProvenance -Id $mvoId -AttributeId 42).Sources |
            Where-Object State -eq 'Outranked'

        Lists the Synchronisation Rule mappings that could contribute to attribute 42 but are
        currently losing Attribute Priority resolution.

    .EXAMPLE
        Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue jsmith |
            Get-JIMMetaverseObjectProvenance

        Pipes a Metaverse Object into the cmdlet to get its value provenance summary.

    .LINK
        Get-JIMMetaverseObject

    .LINK
        Get-JIMMetaverseAttributePriority

    .LINK
        Get-JIMMetaverseObjectChangeHistory
    #>
    [CmdletBinding(DefaultParameterSetName = 'Summary')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByAttributeName')]
        [ValidateNotNullOrEmpty()]
        [string]$AttributeName,

        [Parameter(Mandatory, ParameterSetName = 'ByAttributeId')]
        [int]$AttributeId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'Summary') {
            Write-Verbose "Getting value provenance summary for Metaverse Object $Id"
            Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id/provenance"
            return
        }

        $resolvedAttributeId = $AttributeId

        if ($PSCmdlet.ParameterSetName -eq 'ByAttributeName') {
            Write-Verbose "Resolving attribute '$AttributeName' on Metaverse Object $Id"
            $summary = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id/provenance"
            if (-not $summary) {
                # Invoke-JIMApi already wrote the error (e.g. Metaverse Object not found).
                return
            }

            $match = $summary.Attributes | Where-Object { $_.AttributeName -eq $AttributeName }
            if (-not $match) {
                Write-Error "No attribute named '$AttributeName' with a recorded value was found on Metaverse Object $Id."
                return
            }
            $resolvedAttributeId = $match.AttributeId
        }

        Write-Verbose "Getting value provenance for Metaverse Object $Id, attribute $resolvedAttributeId"
        Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id/attributes/$resolvedAttributeId/provenance"
    }
}
