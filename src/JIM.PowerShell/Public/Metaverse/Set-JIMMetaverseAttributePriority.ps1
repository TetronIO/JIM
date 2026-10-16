# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMMetaverseAttributePriority {
    <#
    .SYNOPSIS
        Replaces a Metaverse Attribute's import priority order.

    .DESCRIPTION
        Transactionally renumbers the priorities of all import contributions to a Metaverse
        Attribute for a given Metaverse Object Type. -MappingId must list every current
        contributing Synchronisation Rule Mapping for the Attribute exactly once, in the
        desired priority order (highest first). The one exception is a mapping whose
        Synchronisation Rule is being deleted (its contributed-values recall has not finished
        yet): it may be left out, and stays at the bottom of the order. A refused order names
        the mappings it is missing and any listed mapping that is not a contributor. To
        reposition a single mapping without restating the whole list, use
        Move-JIMMetaverseAttributePriority instead.

    .PARAMETER AttributeId
        The ID of the Metaverse Attribute.

    .PARAMETER ObjectTypeId
        The ID of the Metaverse Object Type the priority order applies to.

    .PARAMETER MappingId
        Every contributing Attribute Flow mapping ID, once each, highest priority first. A mapping whose
        Synchronisation Rule is being deleted may be left out; it stays at the bottom.

    .PARAMETER NullIsValueMappingId
        The mapping IDs, from -MappingId, whose "Null is a value" flag is set, so that source can assert the
        attribute has no value rather than defer to the next source. Every other mapping has the flag cleared.

    .PARAMETER PassThru
        Returns the resulting priority order.

    .OUTPUTS
        With -PassThru, the resulting priority order; otherwise nothing.

    .EXAMPLE
        Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 45, 12, 78

        Sets mapping 45 as highest priority, then 12, then 78.

    .EXAMPLE
        Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 45, 12 -NullIsValueMappingId 45 -PassThru

        Sets mapping 45 as highest priority with "Null is a value" enabled, then 12.

    .LINK
        Get-JIMMetaverseAttributePriority
        Move-JIMMetaverseAttributePriority
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.AttributePriorityOrderDto')]
    param(
        [Parameter(Mandatory)]
        [int]$AttributeId,

        [Parameter(Mandatory)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory)]
        [int[]]$MappingId,

        [int[]]$NullIsValueMappingId,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess("Attribute $AttributeId (Object Type $ObjectTypeId)", "Set priority order")) {
            Write-Verbose "Setting attribute priority order for attribute $AttributeId on object type $ObjectTypeId"

            $contributors = foreach ($id in $MappingId) {
                @{
                    mappingId   = $id
                    nullIsValue = [bool]($NullIsValueMappingId -and $NullIsValueMappingId -contains $id)
                }
            }
            $body = @{ contributors = @($contributors) }

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$AttributeId/priorities/$ObjectTypeId" -Method 'PUT' -Body $body
                Write-Verbose "Set attribute priority order for attribute $AttributeId"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to set attribute priority order: $_"
            }
        }
    }
}
