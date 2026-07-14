# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMetaverseAttributeDeletionPreview {
    <#
    .SYNOPSIS
        Previews the impact of deleting a custom Metaverse Attribute.

    .DESCRIPTION
        Returns a non-destructive assessment of what deleting a custom Metaverse Attribute would
        entail: whether it is built-in, how many Metaverse Objects hold a stored value for it (a
        hard block on deletion), the per-Object-Type value breakdown, and the configuration
        references (bindings, Attribute Flows, scoping criteria, Object Matching Rules) that would
        be cascade-removed. Use this to inspect an attribute before calling
        Remove-JIMMetaverseAttribute.

    .PARAMETER Id
        The unique identifier of the Attribute to preview.

    .PARAMETER InputObject
        Attribute object to preview (from pipeline).

    .OUTPUTS
        PSCustomObject describing the deletion impact (BlockedByValues, RequiresConfirmation,
        TotalObjectsWithValues, ObjectTypeValueCounts, References, and so on).

    .EXAMPLE
        Get-JIMMetaverseAttributeDeletionPreview -Id 42

        Previews the impact of deleting attribute 42.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Name "CostCentre" | Get-JIMMetaverseAttributeDeletionPreview

        Previews the impact of deleting the CostCentre attribute (from the pipeline).

    .LINK
        Remove-JIMMetaverseAttribute
        Get-JIMMetaverseAttribute
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $attrId = if ($InputObject) { $InputObject.id } else { $Id }

        try {
            Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId/deletion-preview"
        }
        catch {
            Write-Error "Failed to preview deletion of Metaverse Attribute ${attrId}: $_"
        }
    }
}
