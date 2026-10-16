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
        The ID of the Role to remove the member from.

    .PARAMETER MetaverseObjectId
        The ID of the Metaverse Object to remove.

    .PARAMETER InputObject
        A Metaverse Object from the pipeline, for example from Get-JIMMetaverseObject.

    .PARAMETER Force
        Skips the confirmation prompt.

    .PARAMETER ChangeReason
        Why the change was made, recorded on the audit Activity and shown in the Role's Configuration Change
        History.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

        Removes the specified metaverse object from the role (prompts for confirmation).

    .EXAMPLE
        Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..." -Force

        Removes the member without confirmation.

    .EXAMPLE
        Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..." -Force -ChangeReason "Offboarding (CHG0128)"

        Removes a member without confirmation and records a reason with the change.

    .EXAMPLE
        Get-JIMRoleMember -RoleId 2 | Where-Object { $_.displayName -eq "Bob" } | Remove-JIMRoleMember -RoleId 2 -Force

        Removes a specific member from a role by name.

    .LINK
        Get-JIMRole
        Get-JIMRoleMember
        Add-JIMRoleMember
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType([void])]
    param(
        [Parameter(Mandatory)]
        [int]$RoleId,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [Guid]$MetaverseObjectId,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$Force,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $objectId = if ($InputObject) { $InputObject.id } else { $MetaverseObjectId }

        if (Confirm-JIMAction -Cmdlet $PSCmdlet -Target $objectId -Action "Remove from Role $RoleId" -Force:$Force) {
            Write-Verbose "Removing metaverse object $objectId from role $RoleId"

            try {
                $endpoint = "/api/v1/security/roles/$RoleId/members/$objectId"
                if ($ChangeReason) {
                    $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                $null = Invoke-JIMApi -Endpoint $endpoint -Method 'DELETE'
                Write-Verbose "Removed metaverse object $objectId from role $RoleId"
            }
            catch {
                Write-Error "Failed to remove role member: $_"
            }
        }
    }
}
