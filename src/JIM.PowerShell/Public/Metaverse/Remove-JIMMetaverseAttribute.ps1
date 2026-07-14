# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Removes a custom Metaverse Attribute from JIM.

    .DESCRIPTION
        Deletes a custom Metaverse Attribute. The cmdlet first fetches a deletion preview to decide
        how to proceed:

        - If any Metaverse Object holds a stored value for the attribute, deletion is a hard block;
          the cmdlet refuses and reports the affected object count. Clear the values first.
        - If the attribute is referenced by configuration (bindings, Attribute Flows, scoping
          criteria, Object Matching Rules) but holds no stored values, those references are
          cascade-removed. This is guarded server-side by a type-the-name confirmation, which the
          cmdlet satisfies automatically once you confirm (or pass -Force); the cascade is recorded
          as child Activities of the deletion.
        - If the attribute is unreferenced, it is simply removed.

        Built-in attributes cannot be deleted.

    .PARAMETER Id
        The unique identifier of the Attribute to delete.

    .PARAMETER InputObject
        Attribute object to delete (from pipeline).

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the object's
        configuration change history.

    .PARAMETER Force
        Skips the confirmation prompt. The server-side type-the-name safeguard is still satisfied
        by the cmdlet; -Force only suppresses the interactive PowerShell prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMMetaverseAttribute -Id 1

        Removes the Attribute with ID 1 after confirmation, cascade-removing any references.

    .EXAMPLE
        Remove-JIMMetaverseAttribute -Id 1 -Force

        Removes the Attribute with ID 1 without an interactive prompt.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Name "CustomAttr" | Remove-JIMMetaverseAttribute

        Removes an attribute from the pipeline.

    .LINK
        Get-JIMMetaverseAttribute
        Get-JIMMetaverseAttributeDeletionPreview
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

        # Fetch the deletion preview to decide how to proceed and to obtain the exact attribute name
        # for the server's type-the-name confirmation.
        try {
            $impact = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId/deletion-preview"
        }
        catch {
            Write-Error "Failed to evaluate deletion of Metaverse Attribute ${attrId}: $_"
            return
        }

        if ($impact.builtIn) {
            Write-Error "Cannot delete '$($impact.attributeName)': built-in attributes cannot be deleted."
            return
        }

        if ($impact.blockedByValues) {
            Write-Error "Cannot delete '$($impact.attributeName)': $($impact.totalObjectsWithValues) Metaverse Object(s) hold a stored value for it. Clear the values first, then retry."
            return
        }

        $refCount = if ($impact.references) { @($impact.references).Count } else { 0 }
        $action = if ($refCount -gt 0) {
            "Remove Metaverse Attribute and cascade-remove $refCount reference(s)"
        } else {
            "Remove Metaverse Attribute"
        }

        if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) {
            $ConfirmPreference = 'None'
        }

        if ($PSCmdlet.ShouldProcess($impact.attributeName, $action)) {
            Write-Verbose "Removing Metaverse Attribute: $($impact.attributeName) (ID $attrId); references to cascade: $refCount"

            try {
                # Always send confirmationName: the server requires it to match when references
                # exist and ignores it otherwise.
                $query = "confirmationName=$([uri]::EscapeDataString([string]$impact.attributeName))"
                if ($ChangeReason) {
                    $query += "&changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                $null = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$attrId`?$query" -Method 'DELETE'

                Write-Verbose "Removed Metaverse Attribute: $($impact.attributeName) (ID $attrId)"
            }
            catch {
                Write-Error "Failed to remove Metaverse Attribute '$($impact.attributeName)': $_"
            }
        }
    }
}
