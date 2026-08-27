# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMContributedValuesImpactText {
    <#
    .SYNOPSIS
        Builds the impact sentence for a deletion that affects contributed Metaverse attribute values (#1537).

    .DESCRIPTION
        Turns a contributed-values summary (from the contributed-values-summary endpoints) into the sentence
        Remove-JIMSyncRule and Remove-JIMSyncRuleMapping include in their ShouldProcess confirmation text, so
        an administrator sees the impact of the deletion before agreeing to it. Returns nothing when the
        summary reports no contributed values (or when there is no summary), in which case the cmdlets fall
        back to their plain confirmation text.

    .PARAMETER Summary
        The contributed-values summary object: Attributes (per-attribute breakdown), TotalValues and
        TotalObjects.

    .PARAMETER KeepContributedValues
        The administrator chose to keep the values: the sentence warns that they remain in place with no
        provenance and can never be recalled, instead of describing the recall.

    .PARAMETER DeferredRecall
        The recall happens at the next Full Synchronisation of the contributing system (Attribute Flow mapping
        deletions) rather than via a queued Worker task (Synchronisation Rule deletions).

    .OUTPUTS
        The impact sentence as a string, or nothing when the summary reports no contributed values.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        $Summary,

        [switch]$KeepContributedValues,

        [switch]$DeferredRecall
    )

    if (-not $Summary -or -not $Summary.TotalValues -or $Summary.TotalValues -le 0) {
        return
    }

    # Invariant culture keeps the thousands separator deterministic wherever the module runs.
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $attributeCount = @($Summary.Attributes).Count.ToString('N0', $invariant)
    $objectCount = ([int]$Summary.TotalObjects).ToString('N0', $invariant)
    $counts = "$attributeCount attribute(s) across $objectCount Metaverse Object(s)"

    if ($KeepContributedValues) {
        return "$counts will be KEPT with no provenance; nothing will ever recall these values"
    }

    if ($DeferredRecall) {
        return "$counts will be recalled at the next Full Synchronisation of the contributing system"
    }

    return "$counts will be recalled"
}
