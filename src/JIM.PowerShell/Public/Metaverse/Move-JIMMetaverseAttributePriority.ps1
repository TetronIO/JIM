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
        The ID of the Metaverse Attribute.

    .PARAMETER ObjectTypeId
        The ID of the Metaverse Object Type the priority order applies to.

    .PARAMETER MappingId
        The ID of the contributing Attribute Flow mapping to move.

    .PARAMETER Position
        The position to move it to, counting from 1 for the highest priority.

    .PARAMETER NullIsValue
        Sets the moved mapping's "Null is a value" flag; -NullIsValue:$false clears it. Omit it to leave the flag as
        it is.

    .PARAMETER PassThru
        Returns the resulting priority order.

    .OUTPUTS
        With -PassThru, the resulting priority order; otherwise nothing. AwaitingSynchronisation lists each Connected
        System Object Type the move makes its Connected System's next synchronisation, Delta or Full, process in full,
        with how many objects that is and about how long it takes, and a warning saying the same is written to the
        warning stream either way.

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
    [OutputType('JIM.AttributePriorityOrderDto')]
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

                # The Object Types the move made the next synchronisation process in full (#2615): advised, never blocking.
                Write-JIMAwaitingSynchronisationWarning -AwaitingSynchronisation $response.AwaitingSynchronisation

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
