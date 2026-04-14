# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMRoleMember {
    <#
    .SYNOPSIS
        Removes a Metaverse Object from a security Role in JIM.

    .DESCRIPTION
        Removes a Metaverse Object from the specified security Role. This revokes
        the permissions associated with that Role for the object.

        Safety checks prevent removing yourself from the Administrator role and
        removing the last Administrator, as either action would cause a lockout.

    .PARAMETER RoleId
        The unique identifier (integer) of the Role to remove the member from.

    .PARAMETER MetaverseObjectId
        The unique identifier (GUID) of the Metaverse Object to remove.

    .PARAMETER Force
        Suppresses confirmation prompts.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

        Removes the specified metaverse object from the role (prompts for confirmation).

    .EXAMPLE
        Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..." -Force

        Removes the member without confirmation.

    .EXAMPLE
        Get-JIMRoleMember -RoleId 2 | Where-Object { $_.displayName -eq "Bob" } | Remove-JIMRoleMember -RoleId 2 -Force

        Removes a specific member from a role by name.

    .LINK
        Get-JIMRole
        Get-JIMRoleMember
        Add-JIMRoleMember
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory)]
        [int]$RoleId,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [Guid]$MetaverseObjectId,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $objectId = if ($InputObject) { $InputObject.id } else { $MetaverseObjectId }

        if ($Force -or $PSCmdlet.ShouldProcess($objectId, "Remove from Role $RoleId")) {
            Write-Verbose "Removing metaverse object $objectId from role $RoleId"

            try {
                $null = Invoke-JIMApi -Endpoint "/api/v1/security/roles/$RoleId/members/$objectId" -Method 'DELETE'
                Write-Verbose "Removed metaverse object $objectId from role $RoleId"
            }
            catch {
                Write-Error "Failed to remove role member: $_"
            }
        }
    }
}
