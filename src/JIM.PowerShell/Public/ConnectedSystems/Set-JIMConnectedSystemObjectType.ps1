# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemObjectType {
    <#
    .SYNOPSIS
        Changes a Connected System Object Type.

    .DESCRIPTION
        Changes whether JIM manages a Connected System Object Type, and what happens to the values its objects
        contributed when they are deleted.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER ObjectTypeId
        The ID of the Object Type to change.

    .PARAMETER Selected
        Whether JIM manages objects of this type. Deselecting it takes the type out of management: the next Full
        Import no longer returns its objects, so marks them deleted, and the synchronisation after that disconnects
        them from their Metaverse Objects and applies the Metaverse Object Type's deletion rules, as deselecting a
        partition does. Refused while an enabled Synchronisation Rule uses the Object Type; disable those rules
        first.

    .PARAMETER RemoveContributedAttributesOnObsoletion
        Whether the Metaverse attribute values an object of this type contributed are recalled when the object is
        deleted from the Connected System.

    .PARAMETER PassThru
        Returns the Object Type as changed.

    .OUTPUTS
        With -PassThru, the Object Type as changed; otherwise nothing.

    .EXAMPLE
        Set-JIMConnectedSystemObjectType -ConnectedSystemId 1 -ObjectTypeId 5 -Selected $true

        Marks the object type as selected for management by JIM.

    .EXAMPLE
        Set-JIMConnectedSystemObjectType -ConnectedSystemId 1 -ObjectTypeId 5 -Selected $true -PassThru

        Marks the object type as selected and returns the updated object.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 -ObjectTypes | Where-Object { $_.name -eq "User" } |
            ForEach-Object { Set-JIMConnectedSystemObjectType -ConnectedSystemId 1 -ObjectTypeId $_.id -Selected $true }

        Selects the User object type for management.

    .EXAMPLE
        Get-JIMSyncRule -ConnectedSystemId 1 | Where-Object { $_.connectedSystemObjectTypeId -eq 5 } | Set-JIMSyncRule -Disable
        Set-JIMConnectedSystemObjectType -ConnectedSystemId 1 -ObjectTypeId 5 -Selected $false

        Takes object type 5 out of management. The first line disables every Synchronisation Rule
        bound to it, which deselecting requires. On the next Full Import every Connected System Object
        of that type is marked as deleted, and the following synchronisation disconnects them all and
        applies their Metaverse Objects' deletion rules. Run the first line without the final pipeline
        stage to see which Synchronisation Rules it would disable, and preview the deselection first with
        New-JIMConfigurationChangePreview -ConnectedSystemId 1 -SchemaObjectType @(@{ objectTypeId = 5; selected = $false }) -Wait

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystemAttribute
        Set-JIMSyncRule
        New-JIMConfigurationChangePreview
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.ConnectedSystemObjectTypeDto')]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ObjectTypeId,

        [Parameter()]
        [bool]$Selected,

        [Parameter()]
        [bool]$RemoveContributedAttributesOnObsoletion,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Build update body
        $body = @{}

        if ($PSBoundParameters.ContainsKey('Selected')) {
            $body.selected = $Selected
        }

        if ($PSBoundParameters.ContainsKey('RemoveContributedAttributesOnObsoletion')) {
            $body.removeContributedAttributesOnObsoletion = $RemoveContributedAttributesOnObsoletion
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSCmdlet.ShouldProcess("Object Type $ObjectTypeId in Connected System $ConnectedSystemId", "Update")) {
            Write-Verbose "Updating Object Type: $ObjectTypeId in Connected System: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Object Type: $ObjectTypeId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Object Type: $_"
            }
        }
    }
}
