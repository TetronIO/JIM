# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemAttribute {
    <#
    .SYNOPSIS
        Changes attributes of a Connected System Object Type.

    .DESCRIPTION
        Changes one attribute, or in bulk several at once with -AttributeUpdates: whether JIM manages it, whether it
        identifies objects, whether its values are case-exact, the step of time a date is held to, and where the
        Connector allows it, its data type.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER ObjectTypeId
        The ID of the Object Type the attributes belong to.

    .PARAMETER AttributeId
        The ID of the attribute to change.

    .PARAMETER AttributeUpdates
        The attributes to change, as a hashtable keyed by attribute ID whose values are hashtables of the settings
        to change (selected, isExternalId, isSecondaryExternalId, caseExact, dateTimeResolution); the examples show one.

    .PARAMETER Selected
        Whether JIM imports and exports the attribute.

    .PARAMETER IsExternalId
        Whether the attribute uniquely and permanently identifies an object of this type, such as objectGUID or an
        employee ID.

    .PARAMETER IsSecondaryExternalId
        Whether the Connected System also identifies an object by the attribute, as an LDAP directory does by
        distinguished name.

    .PARAMETER Type
        Replaces the data type inferred from the schema. Accepted only where the Connector says its schema cannot
        state a type for certain: a delimited file names no types, and Oracle has one numeric type, so a NUMBER
        column may be a whole number, a counter or a fractional figure. Refused once a Synchronisation Rule uses the
        attribute or it holds values. Integer is the Number data type, as in New-JIMMetaverseAttribute. A Boolean
        holds one value, so Boolean also makes the attribute single-valued. Cannot be set with -AttributeUpdates.

    .PARAMETER CaseExact
        Whether the attribute's values are case-exact: $true when values differing only in case identify different
        things, $false when they are the same value. Only a Text attribute can be declared case-exact. Setting it
        makes the declaration the administrator's, so a schema refresh keeps it. Offered on every Connector.

    .PARAMETER DateTimeResolution
        The finest step of time the Connected System holds this DateTime attribute's values to. JIM sends each date
        floored to it and compares at it, so a difference the Connected System cannot hold is never exported again and
        again. Discovered by the Connector (Second for Active Directory and SCIM, a SQL column's own precision);
        setting it makes the resolution the administrator's, so a schema refresh keeps it. Only a DateTime attribute
        has one.

    .PARAMETER PassThru
        Returns the attribute as changed, or with -AttributeUpdates, the outcome of the change.

    .OUTPUTS
        With -PassThru, the attribute as changed, or with -AttributeUpdates, what was changed and what failed;
        otherwise nothing. An attribute -AttributeUpdates could not change is written to the warning stream.

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

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 12 -CaseExact $true

        Declares that an attribute's values are case-exact, so JBloggs and jbloggs are different values. A schema
        refresh keeps this declaration rather than restating what the Connected System's schema says.

    .EXAMPLE
        Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 8 -AttributeId 41 -DateTimeResolution Millisecond

        Tells JIM that a SCIM service provider keeps this date attribute's milliseconds, so JIM sends them rather than
        the whole seconds SCIM discovery assumes.

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystemObjectType
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Single')]
    [OutputType('JIM.ConnectedSystemAttributeDto', ParameterSetName = 'Single')]
    [OutputType('JIM.BulkUpdateConnectedSystemAttributesResponse', ParameterSetName = 'Bulk')]
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

        [Parameter(ParameterSetName = 'Single')]
        [bool]$CaseExact,

        [Parameter(ParameterSetName = 'Single')]
        [ValidateSet('Microsecond', 'TenMicroseconds', 'HundredMicroseconds', 'Millisecond', 'TenMilliseconds', 'HundredMilliseconds', 'Second', 'Minute', 'Day')]
        [string]$DateTimeResolution,

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

            if ($PSBoundParameters.ContainsKey('CaseExact')) {
                $body.caseExact = $CaseExact
            }

            if ($PSBoundParameters.ContainsKey('DateTimeResolution')) {
                $body.dateTimeResolution = $DateTimeResolution
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
