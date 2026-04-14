# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMRoleMember {
    <#
    .SYNOPSIS
        Gets the members of a security Role in JIM.

    .DESCRIPTION
        Retrieves Metaverse Objects that are statically assigned to a security Role.
        Members are returned sorted by display name.

    .PARAMETER RoleId
        The unique identifier (integer) of the Role whose members to retrieve.

    .PARAMETER InputObject
        Role object from the pipeline (e.g., from Get-JIMRole).

    .OUTPUTS
        PSCustomObject representing metaverse object(s) that are members of the Role.

    .EXAMPLE
        Get-JIMRoleMember -RoleId 1

        Lists members of the role with ID 1.

    .EXAMPLE
        Get-JIMRole -Name "Administrator" | Get-JIMRoleMember

        Lists members of the Administrator role using the pipeline.

    .EXAMPLE
        Get-JIMRole | Get-JIMRoleMember

        Lists members of all roles.

    .LINK
        Get-JIMRole
        Add-JIMRoleMember
        Remove-JIMRoleMember
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$RoleId,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $id = if ($InputObject) { $InputObject.id } else { $RoleId }

        Write-Verbose "Getting members of role: $id"

        try {
            $response = Invoke-JIMApi -Endpoint "/api/v1/security/roles/$id/members"

            foreach ($member in $response) {
                $member
            }
        }
        catch {
            Write-Error "Failed to get role members: $_"
        }
    }
}
