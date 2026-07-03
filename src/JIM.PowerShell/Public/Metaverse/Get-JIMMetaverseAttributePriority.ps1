# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMetaverseAttributePriority {
    <#
    .SYNOPSIS
        Gets a Metaverse Attribute's import priority order.

    .DESCRIPTION
        Returns the ordered list of import contributions to a Metaverse Attribute for a given
        Metaverse Object Type, highest priority first. When more than one Connected System
        contributes to the same Metaverse Attribute, the highest-priority contributor still
        connected wins. Disabled Synchronisation Rules are included; they hold position but
        never contribute during resolution.

    .PARAMETER AttributeId
        The unique identifier of the Metaverse Attribute.

    .PARAMETER ObjectTypeId
        The unique identifier of the Metaverse Object Type that scopes the priority list.

    .OUTPUTS
        PSCustomObject representing the Attribute's priority order and contributors.

    .EXAMPLE
        Get-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1

        Gets the priority order for Attribute 12 on Object Type 1.

    .EXAMPLE
        (Get-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1).Contributors

        Lists just the contributing mappings, in priority order.

    .LINK
        Set-JIMMetaverseAttributePriority
        Move-JIMMetaverseAttributePriority
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$AttributeId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ObjectTypeId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting attribute priority order for attribute $AttributeId on object type $ObjectTypeId"
        Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$AttributeId/priorities/$ObjectTypeId"
    }
}
