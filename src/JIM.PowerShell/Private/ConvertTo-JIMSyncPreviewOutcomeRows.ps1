# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMSyncPreviewOutcomeRows {
    <#
    .SYNOPSIS
        Flattens a Sync Preview's outcome tree into a depth-ordered list of rows.

    .DESCRIPTION
        A Sync Preview's OutcomeTree is a recursive structure (each node carries its own
        Children), which is awkward to read or filter from the console. This walks the tree
        depth-first and emits one row per node, in display order, so the result can be piped
        straight into Format-Table without the caller having to recurse themselves.

        Each row carries Depth (0 for a root node, incrementing per level), the outcome's
        Target (the node's TargetEntityDescription, falling back to TargetEntityId when no
        description was captured) and SyncRule (SyncRuleName, falling back to SyncRuleId),
        alongside OutcomeType, DetailCount, DetailMessage and StagedChangeType unchanged.

    .PARAMETER Nodes
        The outcome nodes to flatten (a preview's OutcomeTree, or a node's Children).

    .PARAMETER Depth
        Internal. The depth of the nodes passed in Nodes; callers should omit this.

    .OUTPUTS
        PSCustomObject with Depth, OutcomeType, Target, SyncRule, DetailCount, DetailMessage
        and StagedChangeType, one per node, in tree display order.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowNull()]
        [object[]]$Nodes,

        [int]$Depth = 0
    )

    foreach ($node in $Nodes) {
        if ($null -eq $node) {
            continue
        }

        [PSCustomObject]@{
            Depth            = $Depth
            OutcomeType      = $node.OutcomeType
            Target           = $node.TargetEntityDescription ?? $node.TargetEntityId
            SyncRule         = $node.SyncRuleName ?? $node.SyncRuleId
            DetailCount      = $node.DetailCount
            DetailMessage    = $node.DetailMessage
            StagedChangeType = $node.StagedChangeType
        }

        if ($node.Children -and @($node.Children).Count -gt 0) {
            ConvertTo-JIMSyncPreviewOutcomeRows -Nodes $node.Children -Depth ($Depth + 1)
        }
    }
}
