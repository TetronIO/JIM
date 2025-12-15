function New-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Creates a new Metaverse Attribute in JIM.

    .DESCRIPTION
        Creates a new attribute definition in the Metaverse schema.
        Attributes define what data can be stored on Metaverse objects.

    .PARAMETER Name
        The name of the new attribute. Must be unique.

    .PARAMETER Type
        The data type of the attribute.
        Valid values: Text, Integer, DateTime, Boolean, Reference, Guid, Binary

    .PARAMETER AttributePlurality
        Whether the attribute is single-valued or multi-valued.
        Valid values: SingleValued, MultiValued
        Defaults to SingleValued.

    .PARAMETER ObjectTypeIds
        Optional array of Object Type IDs to associate this attribute with.
        If not specified, the attribute can be associated with object types later.

    .OUTPUTS
        PSCustomObject representing the created Attribute.

    .EXAMPLE
        New-JIMMetaverseAttribute -Name "EmployeeId" -Type Text

        Creates a new text attribute named 'EmployeeId'.

    .EXAMPLE
        New-JIMMetaverseAttribute -Name "Manager" -Type Reference

        Creates a new reference attribute for storing manager relationships.

    .EXAMPLE
        New-JIMMetaverseAttribute -Name "PhoneNumbers" -Type Text -AttributePlurality MultiValued

        Creates a multi-valued text attribute.

    .EXAMPLE
        New-JIMMetaverseAttribute -Name "Department" -Type Text -ObjectTypeIds 1,2

        Creates an attribute and associates it with object types 1 and 2.

    .LINK
        Get-JIMMetaverseAttribute
        Set-JIMMetaverseAttribute
        Remove-JIMMetaverseAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory)]
        [ValidateSet('Text', 'Integer', 'DateTime', 'Boolean', 'Reference', 'Guid', 'Binary')]
        [string]$Type,

        [Parameter()]
        [ValidateSet('SingleValued', 'MultiValued')]
        [string]$AttributePlurality = 'SingleValued',

        [Parameter()]
        [int[]]$ObjectTypeIds
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Build request body
        $body = @{
            name = $Name
            type = $Type
            attributePlurality = $AttributePlurality
        }

        if ($ObjectTypeIds) {
            $body.objectTypeIds = $ObjectTypeIds
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Metaverse Attribute")) {
            Write-Verbose "Creating Metaverse Attribute: $Name"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes" -Method 'POST' -Body $body

                Write-Verbose "Created Metaverse Attribute: $Name with ID: $($result.id)"

                $result
            }
            catch {
                Write-Error "Failed to create Metaverse Attribute: $_"
            }
        }
    }
}
