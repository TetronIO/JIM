function Set-JIMConnectedSystemObjectType {
    <#
    .SYNOPSIS
        Updates properties of a Connected System Object Type in JIM.

    .DESCRIPTION
        Updates properties of an object type within a Connected System's schema.
        Use this to mark object types as selected for management by JIM.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ObjectTypeId
        The unique identifier of the Object Type to update.

    .PARAMETER Selected
        Whether the object type should be managed by JIM.
        When set to $true, JIM will synchronise objects of this type.

    .PARAMETER RemoveContributedAttributesOnObsoletion
        Whether to remove contributed attributes from the Metaverse object
        when a Connected System object is obsoleted.

    .PARAMETER PassThru
        If specified, returns the updated object type.

    .OUTPUTS
        If -PassThru is specified, returns the updated Object Type object.

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

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystemAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
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
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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
