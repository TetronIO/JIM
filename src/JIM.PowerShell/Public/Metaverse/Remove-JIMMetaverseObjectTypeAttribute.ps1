# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMMetaverseObjectTypeAttribute {
    <#
    .SYNOPSIS
        Unassigns a custom Metaverse Attribute from a Metaverse Object Type.

    .DESCRIPTION
        Removes the binding between a custom Metaverse Attribute and a Metaverse Object Type. The
        cmdlet first fetches an unassign preview to decide how to proceed:

        - If any Metaverse Object of the target type holds a stored value for the attribute,
          unassignment is a hard block; the cmdlet refuses and reports the affected object count.
          Clear the values first.
        - If Synchronisation Rules targeting the type reference the attribute (Attribute Flows,
          scoping criteria, Object Matching Rules), those type-scoped references are cascade-removed
          alongside the binding. This is guarded server-side by a type-the-name confirmation, which
          the cmdlet satisfies automatically once you confirm (or pass -Force); the cascade is
          recorded as child Activities.
        - If only the plain binding exists, it is simply removed.

        If the attribute is not bound to the Object Type, the cmdlet reports it and does nothing.
        Built-in attributes cannot be unassigned.

    .PARAMETER AttributeId
        The unique identifier of the Metaverse Attribute to unassign.

    .PARAMETER ObjectTypeId
        The unique identifier of the Metaverse Object Type to unassign the attribute from.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the object's
        configuration change history.

    .PARAMETER Force
        Skips the confirmation prompt. The server-side type-the-name safeguard is still satisfied
        by the cmdlet; -Force only suppresses the interactive PowerShell prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1

        Unassigns attribute 42 from Object Type 1 after confirmation, cascade-removing any
        type-scoped references.

    .EXAMPLE
        Remove-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1 -Force

        Unassigns without an interactive prompt.

    .LINK
        Add-JIMMetaverseObjectTypeAttribute
        Get-JIMMetaverseAttribute
        Remove-JIMMetaverseAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$AttributeId,

        [Parameter(Mandatory)]
        [int]$ObjectTypeId,

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

        # Fetch the unassign preview to decide how to proceed and to obtain the exact attribute name
        # for the server's type-the-name confirmation.
        try {
            $impact = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$AttributeId/object-types/$ObjectTypeId/unassign-preview"
        }
        catch {
            Write-Error "Failed to evaluate unassignment of Metaverse Attribute $AttributeId from Object Type ${ObjectTypeId}: $_"
            return
        }

        if ($impact.builtIn) {
            Write-Error "Cannot unassign '$($impact.attributeName)': built-in attributes cannot be unassigned."
            return
        }

        if (-not $impact.wasBound) {
            Write-Warning "Metaverse Attribute '$($impact.attributeName)' is not bound to Object Type '$($impact.metaverseObjectTypeName)'. Nothing to do."
            return
        }

        if ($impact.blockedByValues) {
            Write-Error "Cannot unassign '$($impact.attributeName)' from '$($impact.metaverseObjectTypeName)': $($impact.objectsWithValues) Metaverse Object(s) of that type hold a stored value for it. Clear the values first, then retry."
            return
        }

        # References include the binding row itself; cascade items are those beyond the plain binding.
        $cascadeCount = if ($impact.references) { @($impact.references | Where-Object { $_.kind -ne 'Binding' }).Count } else { 0 }
        $action = if ($cascadeCount -gt 0) {
            "Unassign Metaverse Attribute and cascade-remove $cascadeCount type-scoped reference(s)"
        } else {
            "Unassign Metaverse Attribute from Object Type"
        }

        if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) {
            $ConfirmPreference = 'None'
        }

        $target = "$($impact.attributeName) -> $($impact.metaverseObjectTypeName)"
        if ($PSCmdlet.ShouldProcess($target, $action)) {
            Write-Verbose "Unassigning Metaverse Attribute '$($impact.attributeName)' from Object Type '$($impact.metaverseObjectTypeName)'; type-scoped references to cascade: $cascadeCount"

            try {
                # Always send confirmationName: the server requires it to match when type-scoped
                # references exist and ignores it otherwise.
                $query = "confirmationName=$([uri]::EscapeDataString([string]$impact.attributeName))"
                if ($ChangeReason) {
                    $query += "&changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                $null = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$AttributeId/object-types/$ObjectTypeId`?$query" -Method 'DELETE'

                Write-Verbose "Unassigned Metaverse Attribute '$($impact.attributeName)' from Object Type '$($impact.metaverseObjectTypeName)'"
            }
            catch {
                Write-Error "Failed to unassign Metaverse Attribute '$($impact.attributeName)' from Object Type '$($impact.metaverseObjectTypeName)': $_"
            }
        }
    }
}
