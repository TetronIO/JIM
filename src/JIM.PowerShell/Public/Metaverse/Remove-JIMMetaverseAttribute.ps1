# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the object's
        configuration change history.

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

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
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
                $endpoint = "/api/v1/metaverse/attributes/$attrId"
                if ($ChangeReason) {
                    $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                $null = Invoke-JIMApi -Endpoint $endpoint -Method 'DELETE'

                Write-Verbose "Removed Metaverse Attribute: $attrId"
            }
            catch {
                Write-Error "Failed to remove Metaverse Attribute: $_"
            }
        }
    }
}
