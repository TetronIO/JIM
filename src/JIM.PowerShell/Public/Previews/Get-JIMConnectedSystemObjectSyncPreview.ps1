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
        would be deprovisioned as a result. A downstream object with no matching export
        Synchronisation Rule is disconnected rather than deprovisioned, and is reported as a
        warning (DownstreamDisconnectOnly) rather than as an outcome node.

        Read HasBlockingErrors before anything else: a preview that surfaced a blocking error
        (for example an Attribute Flow that could not be evaluated) describes a synchronisation
        that would fail, not one that would succeed as shown.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System that owns the object.

    .PARAMETER Id
        The unique identifier (GUID) of the Connected System Object to preview.

    .OUTPUTS
        PSCustomObject with OutcomeTree (the raw, recursive outcome tree), Outcomes (the same
        tree flattened by ConvertTo-JIMSyncPreviewOutcomeRows into a depth-ordered list for
        Format-Table), Inbound (the inbound summary: would the object project or join, and
        what attribute flows would result), ProposedExports (the Pending Exports a real
        synchronisation would stage), Errors, Warnings, HasBlockingErrors and
        AffectedSyncRules.

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
    [OutputType([PSCustomObject])]
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

        [PSCustomObject]@{
            OutcomeTree       = $preview.OutcomeTree
            Outcomes          = @(ConvertTo-JIMSyncPreviewOutcomeRows -Nodes $preview.OutcomeTree)
            Inbound           = $preview.Inbound
            ProposedExports   = $preview.ProposedExports
            Errors            = $preview.Errors
            Warnings          = $preview.Warnings
            HasBlockingErrors = $preview.HasBlockingErrors
            AffectedSyncRules = $preview.AffectedSyncRules
        }
    }
}
