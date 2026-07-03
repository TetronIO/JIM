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
        desired priority order (highest first). To reposition a single mapping without
        restating the whole list, use Move-JIMMetaverseAttributePriority instead.

    .PARAMETER AttributeId
        The unique identifier of the Metaverse Attribute.

    .PARAMETER ObjectTypeId
        The unique identifier of the Metaverse Object Type that scopes the priority list.

    .PARAMETER MappingId
        Every current contributing mapping ID, in the desired priority order (highest first).

    .PARAMETER NullIsValueMappingId
        Mapping IDs (from -MappingId) that should have their "Null is a value" flag set, so an
        authoritative source can positively assert "no value" rather than falling through to
        the next-priority source. Mappings not listed here have the flag cleared.

    .PARAMETER PassThru
        If specified, returns the resulting priority order.

    .OUTPUTS
        If -PassThru is specified, returns the resulting priority order.

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
    [OutputType([PSCustomObject])]
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
