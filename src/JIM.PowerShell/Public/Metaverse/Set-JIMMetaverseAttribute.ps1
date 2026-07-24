# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Updates an existing custom Metaverse Attribute in JIM.

    .DESCRIPTION
        Updates a custom Metaverse Attribute. Changes are routed to the correct endpoint:

        - Name, RenderingHint and StandardMappings are updated together via the attribute endpoint.
        - Type and AttributePlurality are updated via the dedicated schema endpoint. Because the
          schema change is refused while any Metaverse Object holds a stored value for the
          attribute, supplying either -Type or -AttributePlurality sends both (the unspecified one
          is read from the attribute's current schema).

        Object Type bindings are not changed here; use Add-JIMMetaverseObjectTypeAttribute and
        Remove-JIMMetaverseObjectTypeAttribute. Built-in attributes cannot be modified.

    .PARAMETER Id
        The unique identifier of the Attribute to update.

    .PARAMETER InputObject
        Attribute object to update (from pipeline).

    .PARAMETER Name
        The new name for the Attribute. Subject to a case-insensitive uniqueness check.

    .PARAMETER RenderingHint
        The rendering hint for multi-valued attributes.
        Valid values: Default, Table, ChipSet, List

    .PARAMETER Type
        The new data type for the attribute.
        Valid values: Text, Integer, LongNumber, Decimal, DateTime, Boolean, Reference, Guid, Binary

    .PARAMETER AttributePlurality
        The new plurality setting.
        Valid values: SingleValued, MultiValued

    .PARAMETER StandardMappings
        The attribute's full set of Standard Mappings, replacing any existing ones; pass an empty
        array (@()) to clear them. Each element is a hashtable with a Standard ('Scim', 'Ldap' or
        'Jim'), a CounterpartName (the equivalent attribute name in that standard), and optional
        Notes. Standard Mappings are guidance only and never affect synchronisation.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the object's
        configuration change history.

    .PARAMETER PassThru
        If specified, returns the updated Attribute object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Attribute object.

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

    .LINK
        Get-JIMMetaverseAttribute
        New-JIMMetaverseAttribute
        Remove-JIMMetaverseAttribute
        Add-JIMMetaverseObjectTypeAttribute
        Remove-JIMMetaverseObjectTypeAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
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
                $pluralityValue = if ($PSBoundParameters.ContainsKey('AttributePlurality')) { $AttributePlurality } else { $current.attributePlurality }

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
