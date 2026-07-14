# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Updates an existing custom Metaverse Attribute in JIM.

    .DESCRIPTION
        Updates a custom Metaverse Attribute. Changes are routed to the correct endpoint:

        - Name and RenderingHint are updated together via the attribute endpoint.
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
        Valid values: Text, Integer, LongNumber, DateTime, Boolean, Reference, Guid, Binary

    .PARAMETER AttributePlurality
        The new plurality setting.
        Valid values: SingleValued, MultiValued

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
        [ValidateSet('Text', 'Integer', 'LongNumber', 'DateTime', 'Boolean', 'Reference', 'Guid', 'Binary')]
        [string]$Type,

        [Parameter()]
        [ValidateSet('SingleValued', 'MultiValued')]
        [string]$AttributePlurality,

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

        # Name/int maps (AttributeDataType and AttributePlurality enums). The API accepts integers
        # for enum request fields; responses return enum names, so the maps below also normalise a
        # current schema value (name or int) back to its integer for the schema endpoint.
        $typeMap = @{
            'Text' = 1; 'Number' = 2; 'Integer' = 2; 'DateTime' = 3; 'Binary' = 4
            'Reference' = 5; 'Guid' = 6; 'Boolean' = 7; 'LongNumber' = 8
        }
        $pluralityMap = @{ 'SingleValued' = 0; 'MultiValued' = 1 }
        $renderingHintMap = @{ 'Default' = 0; 'Table' = 1; 'ChipSet' = 2; 'List' = 3 }

        $metadataChanged = $PSBoundParameters.ContainsKey('Name') -or $PSBoundParameters.ContainsKey('RenderingHint')
        $schemaChanged = $PSBoundParameters.ContainsKey('Type') -or $PSBoundParameters.ContainsKey('AttributePlurality')

        if (-not $metadataChanged -and -not $schemaChanged) {
            Write-Warning "No updates specified. Provide -Name, -RenderingHint, -Type and/or -AttributePlurality."
            return
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

                $typeValue = if ($PSBoundParameters.ContainsKey('Type')) { $typeMap[$Type] } else { $typeMap["$($current.type)"] }
                $pluralityValue = if ($PSBoundParameters.ContainsKey('AttributePlurality')) { $pluralityMap[$AttributePlurality] } else { $pluralityMap["$($current.attributePlurality)"] }

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
                if ($PSBoundParameters.ContainsKey('RenderingHint')) { $body.renderingHint = $renderingHintMap[$RenderingHint] }
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
