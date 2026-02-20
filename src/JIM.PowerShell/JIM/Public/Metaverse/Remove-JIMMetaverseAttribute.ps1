function Remove-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Removes a Metaverse Attribute from JIM.

    .DESCRIPTION
        Deletes a Metaverse attribute definition from the schema.
        Note: Built-in attributes cannot be deleted.

    .PARAMETER Id
        The unique identifier of the Attribute to delete.

    .PARAMETER InputObject
        Attribute object to delete (from pipeline).

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMMetaverseAttribute -Id 1

        Removes the Attribute with ID 1 after confirmation.

    .EXAMPLE
        Remove-JIMMetaverseAttribute -Id 1 -Force

        Removes the Attribute with ID 1 without confirmation.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Name "CustomAttr" | Remove-JIMMetaverseAttribute

        Removes an attribute from the pipeline.

    .LINK
        Get-JIMMetaverseAttribute
        New-JIMMetaverseAttribute
        Set-JIMMetaverseAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $attrId = if ($InputObject) { $InputObject.id } else { $Id }
        $displayName = if ($InputObject -and $InputObject.name) { $InputObject.name } else { $attrId }

        if ($Force -and -not $Confirm) {
            $ConfirmPreference = 'None'
        }

        if ($PSCmdlet.ShouldProcess($displayName, "Remove Metaverse Attribute")) {
            Write-Verbose "Removing Metaverse Attribute: $attrId"

            try {
                $null = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId" -Method 'DELETE'

                Write-Verbose "Removed Metaverse Attribute: $attrId"
            }
            catch {
                Write-Error "Failed to remove Metaverse Attribute: $_"
            }
        }
    }
}
