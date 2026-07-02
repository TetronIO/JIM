# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Move-JIMMetaverseAttributePriority {
    <#
    .SYNOPSIS
        Repositions a single contributor in a Metaverse Attribute's priority order.

    .DESCRIPTION
        Moves one contributing Synchronisation Rule Mapping to the given 1-based priority
        position for a Metaverse Attribute on a Metaverse Object Type, shuffling the other
        contributors to keep the list contiguous, then renumbering all affected rows in one
        transaction. You state only the new position; JIM keeps the order gap-free and
        duplicate-free. Optionally also updates the mapping's "Null is a value" flag.

    .PARAMETER AttributeId
        The unique identifier of the Metaverse Attribute.

    .PARAMETER ObjectTypeId
        The unique identifier of the Metaverse Object Type that scopes the priority list.

    .PARAMETER MappingId
        The contributing Synchronisation Rule Mapping to move.

    .PARAMETER Position
        The desired 1-based priority position (1 = highest priority).

    .PARAMETER NullIsValue
        When specified, also sets the moved mapping's "Null is a value" flag.

    .PARAMETER PassThru
        If specified, returns the resulting priority order.

    .OUTPUTS
        If -PassThru is specified, returns the resulting priority order.

    .EXAMPLE
        Move-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 78 -Position 1

        Moves mapping 78 to the highest priority position.

    .EXAMPLE
        Move-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 1 -MappingId 78 -Position 2 -NullIsValue -PassThru

        Moves mapping 78 to position 2 and enables "Null is a value" for it.

    .LINK
        Get-JIMMetaverseAttributePriority
        Set-JIMMetaverseAttributePriority
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [int]$AttributeId,

        [Parameter(Mandatory)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$MappingId,

        [Parameter(Mandatory)]
        [int]$Position,

        [switch]$NullIsValue,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess("Mapping $MappingId", "Move to priority position $Position")) {
            Write-Verbose "Moving mapping $MappingId to position $Position for attribute $AttributeId on object type $ObjectTypeId"

            $body = @{ position = $Position }
            if ($PSBoundParameters.ContainsKey('NullIsValue')) {
                $body.nullIsValue = [bool]$NullIsValue
            }

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$AttributeId/priorities/$ObjectTypeId/mappings/$MappingId" -Method 'PUT' -Body $body
                Write-Verbose "Moved mapping $MappingId to position $Position"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to move mapping in attribute priority order: $_"
            }
        }
    }
}
