function Set-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Updates an existing Metaverse Attribute in JIM.

    .DESCRIPTION
        Updates the properties of an existing Metaverse attribute.
        Only the parameters provided will be updated.
        Note: Built-in attributes cannot be modified.

    .PARAMETER Id
        The unique identifier of the Attribute to update.

    .PARAMETER InputObject
        Attribute object to update (from pipeline).

    .PARAMETER Name
        The new name for the Attribute.

    .PARAMETER Type
        The new data type for the attribute.
        Valid values: Text, Integer, DateTime, Boolean, Reference, Guid, Binary

    .PARAMETER AttributePlurality
        The new plurality setting.
        Valid values: SingleValued, MultiValued

    .PARAMETER ObjectTypeIds
        Array of Object Type IDs to associate with this attribute.
        This replaces any existing associations.

    .PARAMETER PassThru
        If specified, returns the updated Attribute object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Attribute object.

    .EXAMPLE
        Set-JIMMetaverseAttribute -Id 1 -Name "UpdatedName"

        Updates the name of the Attribute with ID 1.

    .EXAMPLE
        Set-JIMMetaverseAttribute -Id 1 -ObjectTypeIds 1,2,3 -PassThru

        Associates the attribute with object types 1, 2, and 3 and returns the updated object.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Name "CustomAttr" | Set-JIMMetaverseAttribute -Type Integer

        Updates an attribute from the pipeline to change its type.

    .LINK
        Get-JIMMetaverseAttribute
        New-JIMMetaverseAttribute
        Remove-JIMMetaverseAttribute
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
        [ValidateSet('Text', 'Integer', 'DateTime', 'Boolean', 'Reference', 'Guid', 'Binary')]
        [string]$Type,

        [Parameter()]
        [ValidateSet('SingleValued', 'MultiValued')]
        [string]$AttributePlurality,

        [Parameter()]
        [int[]]$ObjectTypeIds,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $attrId = if ($InputObject) { $InputObject.id } else { $Id }

        # Build update body
        $body = @{}

        if ($Name) {
            $body.name = $Name
        }

        if ($Type) {
            $body.type = $Type
        }

        if ($AttributePlurality) {
            $body.attributePlurality = $AttributePlurality
        }

        if ($PSBoundParameters.ContainsKey('ObjectTypeIds')) {
            $body.objectTypeIds = $ObjectTypeIds
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        $displayName = $Name ?? $attrId

        if ($PSCmdlet.ShouldProcess($displayName, "Update Metaverse Attribute")) {
            Write-Verbose "Updating Metaverse Attribute: $attrId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Metaverse Attribute: $attrId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Metaverse Attribute: $_"
            }
        }
    }
}
