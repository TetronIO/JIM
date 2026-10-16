# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Updates an existing custom Metaverse Attribute in JIM.

    .DESCRIPTION
        Updates a custom Metaverse Attribute. Changes are routed to the correct endpoint:

        - Name, RenderingHint and StandardMappings are updated together via the attribute endpoint.
        - Type and AttributePlurality are updated via the dedicated schema endpoint, which needs
          both, so supplying either sends both (the unspecified one is read from the attribute's
          current schema, except that -Type Boolean alone sends SingleValued, since a Boolean holds
          one value). While any Metaverse Object holds a value for the attribute, the only schema
          change JIM allows is widening it from SingleValued to MultiValued with its type
          unchanged; every value is kept. Any other schema change is refused until the values are
          cleared.

        A rename is refused while an Expression reads the attribute by its current name (a Scoping
        Criterion, Attribute Flow, Object Matching Rule or Example Data Template Expression); the
        error names each one. Change them first. A rename that only changes the case of the name is
        allowed, because Expressions read attribute names ignoring case.

        Object Type bindings are not changed here; use Add-JIMMetaverseObjectTypeAttribute and
        Remove-JIMMetaverseObjectTypeAttribute. Built-in attributes cannot be modified.

    .PARAMETER Id
        The ID of the attribute to change.

    .PARAMETER InputObject
        An attribute from the pipeline, for example from Get-JIMMetaverseAttribute.

    .PARAMETER Name
        A new name for the attribute. It must be unique, ignoring case.

    .PARAMETER RenderingHint
        How the portal shows a multi-valued attribute's values. Ignored for a single-valued attribute.

    .PARAMETER Type
        A new kind of value for the attribute; Integer is the type JIM names Number. JIM refuses a type change while
        any Metaverse Object holds a value for the attribute. A Boolean holds one value, so Boolean without
        -AttributePlurality also makes the attribute single-valued.

    .PARAMETER AttributePlurality
        Whether the attribute holds one value or many. While any Metaverse Object holds a value for the attribute,
        JIM allows only a change from SingleValued to MultiValued, which keeps every value; it refuses MultiValued to
        SingleValued, and refuses MultiValued for a Boolean.

    .PARAMETER StandardMappings
        The attribute's Standard Mappings, replacing any it has; @() clears them. Each is a hashtable with a
        Standard (Scim, Ldap or Jim), a CounterpartName (the attribute's name in that standard) and optional Notes.
        Standard Mappings are guidance only and never affect synchronisation.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded in the attribute's Configuration Change History.

    .PARAMETER PassThru
        Returns the updated attribute.

    .OUTPUTS
        With -PassThru, the updated attribute in full; otherwise nothing.

    .EXAMPLE
        Set-JIMMetaverseAttribute -Id 1 -Name "UpdatedName"

        Renames the Attribute with ID 1.

    .EXAMPLE
        Set-JIMMetaverseAttribute -Id 1 -RenderingHint List -PassThru

        Changes the rendering hint and returns the updated object.

    .EXAMPLE
        Set-JIMMetaverseAttribute -Id 42 -StandardMappings @(@{ Standard = 'Scim'; CounterpartName = 'costCenter'; Notes = 'SCIM Enterprise User extension.' })

        Records how the custom attribute corresponds to its SCIM 2.0 counterpart.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Name "CustomAttr" | Set-JIMMetaverseAttribute -Type Integer

        Changes an attribute's data type (refused if any object holds a stored value).

    .EXAMPLE
        Set-JIMMetaverseAttribute -Id 42 -AttributePlurality MultiValued -ChangeReason "The directory now holds several values"

        Widens a single-valued attribute to multi-valued, keeping the values Metaverse Objects already hold.

    .LINK
        Get-JIMMetaverseAttribute
        New-JIMMetaverseAttribute
        Remove-JIMMetaverseAttribute
        Add-JIMMetaverseObjectTypeAttribute
        Remove-JIMMetaverseObjectTypeAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.MetaverseAttributeDetailDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [ValidateSet('Default', 'Table', 'ChipSet', 'List')]
        [string]$RenderingHint,

        [Parameter()]
        [ValidateSet('Text', 'Integer', 'LongNumber', 'Decimal', 'DateTime', 'Boolean', 'Reference', 'Guid', 'Binary')]
        [string]$Type,

        [Parameter()]
        [ValidateSet('SingleValued', 'MultiValued')]
        [string]$AttributePlurality,

        [Parameter()]
        [AllowEmptyCollection()]
        [array]$StandardMappings,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $attrId = if ($InputObject) { $InputObject.id } else { $Id }

        # Enum request fields are sent as their string names; the API rejects numeric ordinals
        # (JsonStringEnumConverter allowIntegerValues:false, PR #1060). Responses already return
        # enum names, so a value read back from the current schema is used as-is. -Type's
        # ValidateSet exposes 'Integer' as an alias for the AttributeDataType member 'Number';
        # that is normalised where -Type is applied below. Other values are exact member names.

        $metadataChanged = $PSBoundParameters.ContainsKey('Name') -or $PSBoundParameters.ContainsKey('RenderingHint') -or $PSBoundParameters.ContainsKey('StandardMappings')
        $schemaChanged = $PSBoundParameters.ContainsKey('Type') -or $PSBoundParameters.ContainsKey('AttributePlurality')

        if (-not $metadataChanged -and -not $schemaChanged) {
            Write-Warning "No updates specified. Provide -Name, -RenderingHint, -StandardMappings, -Type and/or -AttributePlurality."
            return
        }

        # Validate and normalise Standard Mappings up front, before anything is sent to the API. A supplied
        # list replaces the attribute's full set, so an empty array clears them.
        $mappingsBody = $null
        if ($PSBoundParameters.ContainsKey('StandardMappings')) {
            $validStandards = @('Scim', 'Ldap', 'Jim')
            $mappingsBody = @()
            foreach ($mapping in $StandardMappings) {
                if (-not $mapping.Standard -or [string]$mapping.Standard -notin $validStandards) {
                    Write-Error "Each Standard Mapping requires a Standard of 'Scim', 'Ldap' or 'Jim'."
                    return
                }
                if ([string]::IsNullOrWhiteSpace([string]$mapping.CounterpartName)) {
                    Write-Error "Each Standard Mapping requires a CounterpartName (the equivalent attribute name in the standard)."
                    return
                }
                $entry = @{
                    standard        = [string]$mapping.Standard
                    counterpartName = ([string]$mapping.CounterpartName).Trim()
                }
                if (-not [string]::IsNullOrWhiteSpace([string]$mapping.Notes)) { $entry.notes = ([string]$mapping.Notes).Trim() }
                $mappingsBody += $entry
            }
        }

        $displayName = if ($Name) { $Name } elseif ($InputObject -and $InputObject.name) { $InputObject.name } else { $attrId }

        if (-not $PSCmdlet.ShouldProcess($displayName, "Update Metaverse Attribute")) {
            return
        }

        try {
            # Schema change (type / plurality). The endpoint requires both values, so read the
            # current schema to fill whichever was not supplied.
            if ($schemaChanged) {
                $current = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId"

                $typeValue = if ($PSBoundParameters.ContainsKey('Type')) {
                    if ($Type -eq 'Integer') { 'Number' } else { $Type }
                } else {
                    $current.type
                }
                # A Boolean holds one value, so a change to Boolean that names no plurality makes the attribute
                # single-valued, as the portal does; an explicit multi-valued Boolean is sent and JIM refuses it.
                $pluralityValue = if ($PSBoundParameters.ContainsKey('AttributePlurality')) {
                    $AttributePlurality
                } elseif ($typeValue -eq 'Boolean') {
                    'SingleValued'
                } else {
                    $current.attributePlurality
                }

                $schemaBody = @{
                    type               = $typeValue
                    attributePlurality = $pluralityValue
                }
                if ($ChangeReason) { $schemaBody.changeReason = $ChangeReason }

                Write-Verbose "Changing schema for Metaverse Attribute $attrId (type=$typeValue, plurality=$pluralityValue)"
                $null = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId/schema" -Method 'PATCH' -Body $schemaBody
            }

            # Name / rendering-hint change.
            if ($metadataChanged) {
                $body = @{}
                if ($PSBoundParameters.ContainsKey('Name')) { $body.name = $Name }
                if ($PSBoundParameters.ContainsKey('RenderingHint')) { $body.renderingHint = $RenderingHint }
                if ($null -ne $mappingsBody) { $body.standardMappings = $mappingsBody }
                if ($ChangeReason) { $body.changeReason = $ChangeReason }

                Write-Verbose "Updating name/rendering for Metaverse Attribute $attrId"
                $null = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId" -Method 'PATCH' -Body $body
            }

            if ($PassThru) {
                Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId"
            }
        }
        catch {
            Write-Error "Failed to update Metaverse Attribute: $_"
        }
    }
}
