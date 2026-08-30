# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemDeletionImpactText {
    <#
    .SYNOPSIS
        Builds the impact sentence for a Connected System deletion (#809).

    .DESCRIPTION
        Turns a deletion preview (from the connected-systems/{id}/deletion-preview endpoint) into the
        sentence Remove-JIMConnectedSystem includes in its ShouldProcess confirmation text, so an
        administrator sees the impact of the chosen deletion mode before agreeing to it. Returns nothing
        when there is no preview (the lookup failed), in which case the cmdlet falls back to its plain
        confirmation text.

    .PARAMETER Preview
        The deletion preview object: ConnectedSystemObjectCount, ContributedValueCount,
        ContributedValueObjectCount and MvosWithDeletionRuleCount are the fields used.

    .PARAMETER DeleteImmediately
        The administrator chose the immediate deletion: the sentence warns that the contributed attribute
        values remain in place with no provenance and can never be recalled, and that downstream systems
        are not corrected, instead of describing the deprovisioning run.

    .OUTPUTS
        The impact sentence as a string, or nothing when there is no preview.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        $Preview,

        [switch]$DeleteImmediately
    )

    if (-not $Preview) {
        return
    }

    # Invariant culture keeps the thousands separator deterministic wherever the module runs.
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $csoCount = ([int]$Preview.ConnectedSystemObjectCount).ToString('N0', $invariant)
    $valueCount = [int]$Preview.ContributedValueCount
    $valueCountText = $valueCount.ToString('N0', $invariant)
    $valueObjectCountText = ([int]$Preview.ContributedValueObjectCount).ToString('N0', $invariant)

    if ($DeleteImmediately) {
        $text = "$csoCount Connected System Object(s) will be deleted immediately"
        if ($valueCount -gt 0) {
            $text += "; $valueCountText contributed attribute value(s) across $valueObjectCountText Metaverse Object(s) will be KEPT with no provenance; nothing will ever recall them and downstream systems will not be corrected"
        }
        return $text
    }

    $text = "$csoCount Connected System Object(s) will be deprovisioned through synchronisation"
    if ($valueCount -gt 0) {
        $text += "; $valueCountText contributed attribute value(s) across $valueObjectCountText Metaverse Object(s) will be recalled or handed to surviving contributors"
    }
    $deletionRuleCount = [int]$Preview.MvosWithDeletionRuleCount
    if ($deletionRuleCount -gt 0) {
        $text += "; $($deletionRuleCount.ToString('N0', $invariant)) Metaverse Object(s) will be evaluated for deletion"
    }
    return $text
}
