# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectSyncPreview {
    <#
    .SYNOPSIS
        Previews what synchronising a Connected System Object would do.

    .DESCRIPTION
        Nothing is changed: the preview evaluates the inbound chain (scope, join or
        projection, Attribute Flow) and the outbound decisions the resulting Metaverse Object
        state would produce, and returns what a real synchronisation would do without staging
        or persisting anything.

        Where the object would fall out of scope and disconnect, the preview also walks the
        destructive cascade: whether the Metaverse Object would be deleted or scheduled for
        deletion (per its Type's Deletion Rule), and which downstream Connected System Objects
        would be deprovisioned as a result. A downstream object that would only be disconnected
        is reported as a warning (DownstreamDisconnectOnly); where a matching export
        Synchronisation Rule's Deprovisioning Action is Disconnect, it is also a TargetDisconnected
        outcome node nested under the deletion, naming that rule (SyncRuleName). With no matching
        export Synchronisation Rule there is only the warning. A downstream object
        whose provisioning was never exported has that provisioning cancelled instead (the unsent
        Create Pending Export and the Connected System Object are removed; nothing is exported),
        reported as its own ProvisioningCancelled outcome node nested under the deletion, not as a
        warning.

        Read HasBlockingErrors before anything else: a preview that surfaced a blocking error
        (for example an Attribute Flow that could not be evaluated) describes a synchronisation
        that would fail, not one that would succeed as shown.

    .PARAMETER ConnectedSystemId
        The Connected System that owns the object.

    .PARAMETER Id
        The Connected System Object to preview.

    .OUTPUTS
        What synchronising the object would do, were the real synchronisation to run now, with the outcome tree
        flattened beside it. Check HasBlockingErrors first: a preview that surfaced a blocking error describes a
        synchronisation that would fail.

        JIM.SyncPreview
        Outcomes (object[]): The outcome tree flattened in its own display order, for Format-Table: one row per
            outcome, with Depth, OutcomeType, Target (its description, or its id), SyncRule (its name, or its id),
            DetailCount, DetailMessage and StagedChangeType. A Joined row made by matching also carries
            ObjectMatchingRule (what the rule compares, or its id) and MatchedValue (the value the join would match
            on); both are empty on every other row.

    .EXAMPLE
        Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId 1 -Id "3934ff12-4996-42c0-a396-41e17ac47af7"

        Previews synchronising the object, including any destructive cascade.

    .EXAMPLE
        (Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId 1 -Id $csoId).Outcomes |
            Format-Table Depth, OutcomeType, Target, SyncRule

        Reads the speculative outcome tree as a flat, indentation-free table.

    .LINK
        Get-JIMMetaverseObjectSyncPreview
        Get-JIMConnectedSystemObject
    #>
    [CmdletBinding()]
    [OutputType('JIM.SyncPreview')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Previewing synchronisation of Connected System Object $Id in Connected System $ConnectedSystemId"
        $preview = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/sync-preview"
        if ($null -eq $preview) {
            return
        }

        # The API's own object, so nothing it reports is dropped, with the outcome tree flattened beside it for
        # Format-Table. Rebuilding it from a list of properties lost GeneratedValueProbes when the API added it.
        $preview | Add-Member -NotePropertyName 'Outcomes' -NotePropertyValue @(ConvertTo-JIMSyncPreviewOutcomeRows -Nodes $preview.OutcomeTree) -Force -PassThru
    }
}
