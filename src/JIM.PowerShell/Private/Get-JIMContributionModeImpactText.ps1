# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMContributionModeImpactText {
    <#
    .SYNOPSIS
        Builds the impact sentence for a change of a Metaverse attribute's contribution mode (#3059).

    .DESCRIPTION
        Turns the contribution mode impact (from the contribution-mode-impact endpoint) into the sentence
        Set-JIMMetaverseAttribute includes in its confirmation and -WhatIf text, and in its refusal to leave Accumulate
        without -Force. Returns nothing when the change moves no value.

    .PARAMETER Impact
        The contribution mode impact: ObjectsLosingValues, ValuesNoLongerSupplied, ObjectsKeepingOneContributor and
        ObjectsGainingValues.

    .OUTPUTS
        The impact sentence as a string, or nothing when the change moves no value.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        $Impact
    )

    if (-not $Impact) { return }

    $parts = @()
    if ($Impact.objectsLosingValues -gt 0) {
        $parts += "at least $('{0:N0}' -f [int]$Impact.objectsLosingValues) Metaverse Object(s) lose values"
        if ($Impact.valuesNoLongerSupplied -gt 0) {
            $parts[-1] += " ($('{0:N0}' -f [int]$Impact.valuesNoLongerSupplied) value(s) no contributor supplies any more)"
        }
    }
    if ($Impact.objectsKeepingOneContributor -gt 0) {
        $parts += "$('{0:N0}' -f [int]$Impact.objectsKeepingOneContributor) Metaverse Object(s) keep only their highest-priority contributor's values"
    }
    if ($Impact.objectsGainingValues -gt 0) {
        $parts += "$('{0:N0}' -f [int]$Impact.objectsGainingValues) Metaverse Object(s) gain values from further contributors"
    }

    if ($parts.Count -eq 0) { return }
    "At their next synchronisation, " + ($parts -join '; ') + '.'
}
