function Set-JIMConnectedSystemAttribute {
    <#
    .SYNOPSIS
        Updates properties of a Connected System Attribute in JIM.

    .DESCRIPTION
        Updates properties of an attribute within a Connected System's schema.
        Use this to mark attributes as selected for management, or to designate
        them as external identifiers.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ObjectTypeId
        The unique identifier of the Object Type that contains the attribute.

    .PARAMETER AttributeId
        The unique identifier of the Attribute to update.

    .PARAMETER Selected
        Whether the attribute should be managed by JIM.
        When set to $true, JIM will synchronise this attribute.

    .PARAMETER IsExternalId
        Whether this attribute is the primary external identifier for objects.
        This is typically a unique identifier like objectGUID or employeeId.

    .PARAMETER IsSecondaryExternalId
        Whether this attribute is a secondary external identifier.
        This is typically used for attributes like distinguishedName (DN) in LDAP systems.

    .PARAMETER PassThru
        If specified, returns the updated attribute.

    .OUTPUTS
        If -PassThru is specified, returns the updated Attribute object.

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 10 -Selected $true

        Marks the attribute as selected for management by JIM.

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 10 -IsExternalId $true

        Marks the attribute as the primary external identifier.

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 15 -IsSecondaryExternalId $true -PassThru

        Marks the attribute as a secondary identifier (e.g., DN) and returns the updated object.

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystemObjectType
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$AttributeId,

        [Parameter()]
        [bool]$Selected,

        [Parameter()]
        [bool]$IsExternalId,

        [Parameter()]
        [bool]$IsSecondaryExternalId,

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

        if ($PSBoundParameters.ContainsKey('IsExternalId')) {
            $body.isExternalId = $IsExternalId
        }

        if ($PSBoundParameters.ContainsKey('IsSecondaryExternalId')) {
            $body.isSecondaryExternalId = $IsSecondaryExternalId
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSCmdlet.ShouldProcess("Attribute $AttributeId in Object Type $ObjectTypeId", "Update")) {
            Write-Verbose "Updating Attribute: $AttributeId in Object Type: $ObjectTypeId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId/attributes/$AttributeId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Attribute: $AttributeId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Attribute: $_"
            }
        }
    }
}
