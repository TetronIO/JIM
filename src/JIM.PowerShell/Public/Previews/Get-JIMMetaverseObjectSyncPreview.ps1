# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMetaverseObjectSyncPreview {
    <#
    .SYNOPSIS
        Previews what synchronising from a Metaverse Object's current state would do.

    .DESCRIPTION
        Nothing is changed: the preview evaluates the outbound decisions the Metaverse Object's
        current state would produce, and returns what a real synchronisation would do without
        staging or persisting anything.

        A Metaverse Object is never synchronised itself; its Connected System Objects are. This
        preview therefore evaluates the Metaverse Object as it stands now, outbound only: it has no
        inbound chain of its own, so the response's Inbound is always null, and it carries no
        knowledge of what an inbound synchronisation of one of the Metaverse Object's Connected System
        Objects might change first. For the full inbound-then-outbound chain of one Connected
        System Object, use Get-JIMConnectedSystemObjectSyncPreview instead.

        Read HasBlockingErrors before anything else: a preview that surfaced a blocking error
        describes a synchronisation that would fail, not one that would succeed as shown.

    .PARAMETER Id
        The unique identifier (GUID) of the Metaverse Object to preview.

    .OUTPUTS
        PSCustomObject with OutcomeTree (the raw, recursive outcome tree), Outcomes (the same
        tree flattened by ConvertTo-JIMSyncPreviewOutcomeRows into a depth-ordered list for
        Format-Table), Inbound (always null for this cmdlet), ProposedExports (the Pending
        Exports a real synchronisation would stage), Errors, Warnings, HasBlockingErrors and
        AffectedSyncRules.

    .EXAMPLE
        Get-JIMMetaverseObjectSyncPreview -Id "8f14e45f-ceea-467e-adde-3f8cbb1e4d28"

        Previews what would export from the Metaverse Object as it stands now.

    .EXAMPLE
        (Get-JIMMetaverseObjectSyncPreview -Id $mvoId).Outcomes |
            Format-Table Depth, OutcomeType, Target, SyncRule

        Reads the speculative outcome tree as a flat, indentation-free table.

    .LINK
        Get-JIMConnectedSystemObjectSyncPreview
        Get-JIMMetaverseObject
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Previewing outbound synchronisation from Metaverse Object $Id"
        $preview = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id/sync-preview"
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
