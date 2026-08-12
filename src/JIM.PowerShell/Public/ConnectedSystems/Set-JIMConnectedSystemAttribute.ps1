# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemAttribute {
    <#
    .SYNOPSIS
        Updates properties of one or more Connected System Attributes in JIM.

    .DESCRIPTION
        Updates properties of attributes within a Connected System's schema.
        Use this to mark attributes as selected for management, or to designate
        them as external identifiers.

        Supports two modes:
        - Single: Update a single attribute by ID
        - Bulk: Update multiple attributes in a single operation with a hashtable

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ObjectTypeId
        The unique identifier of the Object Type that contains the attribute(s).

    .PARAMETER AttributeId
        The unique identifier of the Attribute to update (Single mode).

    .PARAMETER AttributeUpdates
        A hashtable of attribute updates for bulk operations.
        Keys are attribute IDs, values are hashtables with properties to update.
        Example: @{ 10 = @{ selected = $true }; 11 = @{ selected = $true } }

    .PARAMETER Selected
        Whether the attribute should be managed by JIM (Single mode).
        When set to $true, JIM will synchronise this attribute.

    .PARAMETER IsExternalId
        Whether this attribute is the primary external identifier for objects (Single mode).
        This is typically a unique identifier like objectGUID or employeeId.

    .PARAMETER IsSecondaryExternalId
        Whether this attribute is a secondary external identifier (Single mode).
        This is typically used for attributes like distinguishedName (DN) in LDAP systems.

    .PARAMETER Type
        Overrides the data type schema discovery inferred for the attribute (Single mode).

        Accepted only where the Connector declares that its schema cannot state a type definitively:
        a delimited file names no types at all, and Oracle has a single numeric type, so a NUMBER
        column may be a whole number, a counter or a fractional figure. Refused once the attribute is
        referenced by a Synchronisation Rule or holds values, because changing the type would
        reinterpret data already imported under the old one.

        'Integer' is the friendly name for the Number data type, matching New-JIMMetaverseAttribute.

        There is no bulk equivalent: the bulk endpoint refuses a request carrying a data type.

    .PARAMETER PassThru
        If specified, returns the updated attribute(s).

    .OUTPUTS
        If -PassThru is specified, returns the updated Attribute object(s).
        In Bulk mode, returns a response object containing:
        - activityId: The ID of the activity created for this bulk operation
        - updatedCount: Number of attributes successfully updated
        - updatedAttributes: List of updated attributes
        - errors: Any errors that occurred (null if none)

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 10 -Selected $true

        Marks a single attribute as selected for management by JIM.

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 10 -IsExternalId $true

        Marks an attribute as the primary external identifier.

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 15 -IsSecondaryExternalId $true -PassThru

        Marks an attribute as a secondary identifier (e.g., DN) and returns the updated object.

    .EXAMPLE
        $updates = @{
            10 = @{ selected = $true }
            11 = @{ selected = $true }
            12 = @{ selected = $true; isExternalId = $true }
        }
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeUpdates $updates

        Bulk updates multiple attributes in a single operation, creating only one Activity record.

    .EXAMPLE
        # Get all attributes and select them all in one operation
        $cs = Get-JIMConnectedSystem -Id 1
        $objectType = $cs.objectTypes | Where-Object { $_.name -eq 'user' }
        $updates = @{}
        foreach ($attr in $objectType.attributes) {
            $updates[$attr.id] = @{ selected = $true }
        }
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId $objectType.id -AttributeUpdates $updates -PassThru

        Selects all attributes on an object type using the bulk update API.

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 10 -Type Integer

        Corrects an Oracle NUMBER column that schema discovery read as a Decimal, so it can flow into
        a built-in whole-number Metaverse Attribute such as Employee Number without an expression.

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystemObjectType
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Single')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Single')]
        [Parameter(Mandatory, ParameterSetName = 'Bulk')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'Single')]
        [Parameter(Mandatory, ParameterSetName = 'Bulk')]
        [int]$ObjectTypeId,

        [Parameter(Mandatory, ParameterSetName = 'Single', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$AttributeId,

        [Parameter(Mandatory, ParameterSetName = 'Bulk')]
        [hashtable]$AttributeUpdates,

        [Parameter(ParameterSetName = 'Single')]
        [bool]$Selected,

        [Parameter(ParameterSetName = 'Single')]
        [bool]$IsExternalId,

        [Parameter(ParameterSetName = 'Single')]
        [bool]$IsSecondaryExternalId,

        [Parameter(ParameterSetName = 'Single')]
        [ValidateSet('Text', 'Integer', 'LongNumber', 'Decimal', 'DateTime', 'Boolean', 'Reference', 'Guid', 'Binary')]
        [string]$Type,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'Bulk') {
            # Bulk update mode
            if ($AttributeUpdates.Count -eq 0) {
                Write-Warning "No attribute updates specified."
                return
            }

            if ($PSCmdlet.ShouldProcess("$($AttributeUpdates.Count) attributes in Object Type $ObjectTypeId", "Bulk Update")) {
                Write-Verbose "Bulk updating $($AttributeUpdates.Count) attributes in Object Type: $ObjectTypeId"

                try {
                    # Convert the hashtable to use string keys for JSON serialization
                    # PowerShell's ConvertTo-Json doesn't support integer keys in hashtables
                    $attributesWithStringKeys = @{}
                    foreach ($key in $AttributeUpdates.Keys) {
                        $attributesWithStringKeys[[string]$key] = $AttributeUpdates[$key]
                    }

                    $body = @{
                        attributes = $attributesWithStringKeys
                    }

                    $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId/attributes" -Method 'PUT' -Body $body

                    Write-Verbose "Bulk update completed: $($result.updatedCount) attributes updated"

                    if ($result.errors) {
                        foreach ($updateError in $result.errors) {
                            Write-Warning "Failed to update attribute $($updateError.attributeId): $($updateError.errorMessage)"
                        }
                    }

                    if ($PassThru) {
                        $result
                    }
                }
                catch {
                    Write-Error "Failed to bulk update attributes: $_"
                }
            }
        }
        else {
            # Single attribute update mode (existing behaviour)
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

            if ($PSBoundParameters.ContainsKey('Type')) {
                # The ValidateSet exposes 'Integer' as a friendly alias for the AttributeDataType member
                # 'Number'; the API takes the enum member name.
                $body.type = if ($Type -eq 'Integer') { 'Number' } else { $Type }
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
}
