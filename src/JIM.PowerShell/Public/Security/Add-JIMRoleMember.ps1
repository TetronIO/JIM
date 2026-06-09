# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Add-JIMRoleMember {
    <#
    .SYNOPSIS
        Adds a Metaverse Object to a security Role in JIM.

    .DESCRIPTION
        Assigns a Metaverse Object as a static member of the specified security Role.
        This grants the object the permissions associated with that Role.

    .PARAMETER RoleId
        The unique identifier (integer) of the Role to add the member to.

    .PARAMETER MetaverseObjectId
        The unique identifier (GUID) of the Metaverse Object to add.

    .PARAMETER InputObject
        Metaverse Object from the pipeline (e.g., from Get-JIMMetaverseObject).

    .OUTPUTS
        None.

    .EXAMPLE
        Add-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

        Adds the specified metaverse object to the role with ID 1.

    .EXAMPLE
        Get-JIMMetaverseObject -Id "a1b2c3d4-..." | Add-JIMRoleMember -RoleId 1

        Adds a metaverse object to a role using the pipeline.

    .EXAMPLE
        $adminRole = Get-JIMRole -Name "Administrator"
        Add-JIMRoleMember -RoleId $adminRole.id -MetaverseObjectId "a1b2c3d4-..."

        Looks up the Administrator role and adds a member to it.

    .LINK
        Get-JIMRole
        Get-JIMRoleMember
        Remove-JIMRoleMember
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory)]
        [int]$RoleId,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [Guid]$MetaverseObjectId,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $objectId = if ($InputObject) { $InputObject.id } else { $MetaverseObjectId }

        if ($PSCmdlet.ShouldProcess($objectId, "Add to Role $RoleId")) {
            Write-Verbose "Adding metaverse object $objectId to role $RoleId"

            try {
                $null = Invoke-JIMApi -Endpoint "/api/v1/security/roles/$RoleId/members/$objectId" -Method 'PUT'
                Write-Verbose "Added metaverse object $objectId to role $RoleId"
            }
            catch {
                Write-Error "Failed to add role member: $_"
            }
        }
    }
}
