# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMMetaverseObjectType {
    <#
    .SYNOPSIS
        Deletes a custom Metaverse Object Type from JIM.

    .DESCRIPTION
        Deletes a custom Metaverse Object Type. The cmdlet first fetches a delete preview to decide
        how to proceed:

        - Built-in types (User, Group) can never be deleted; the cmdlet refuses.
        - If any Metaverse Object of the type exists, deletion is a hard block; the cmdlet refuses and
          reports the object count. Delete the objects first.
        - If any Synchronisation Rule targets the type, deletion is a hard block (deleting the type
          would otherwise cascade-delete the whole rule); the cmdlet refuses and lists the rules.
          Remove the Synchronisation Rules first.
        - Otherwise the type is deleted, cascade-removing its softer references (Predefined Searches,
          Example Data Templates, and custom attribute bindings; the bound attributes themselves are
          kept). This is guarded server-side by a type-the-name confirmation, which the cmdlet
          satisfies automatically once you confirm (or pass -Force); the cascade is recorded as child
          Activities.

    .PARAMETER Id
        The unique identifier of the Metaverse Object Type to delete.

    .PARAMETER Name
        The name of the Metaverse Object Type to delete (resolved to an ID).

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the object's
        configuration change history.

    .PARAMETER Force
        Skips the confirmation prompt. The server-side type-the-name safeguard is still satisfied by
        the cmdlet; -Force only suppresses the interactive PowerShell prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMMetaverseObjectType -Id 5

        Deletes Object Type 5 after confirmation, cascade-removing its Predefined Searches, Example
        Data Template entries and attribute bindings.

    .EXAMPLE
        Remove-JIMMetaverseObjectType -Name 'Device' -Force

        Deletes the 'Device' Object Type without an interactive prompt.

    .LINK
        Get-JIMMetaverseObjectType
        New-JIMMetaverseObjectType
        Set-JIMMetaverseObjectType
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

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

        # Resolve name to ID if using ByName parameter set
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $resolvedType = Resolve-JIMMetaverseObjectType -Name $Name
                $Id = $resolvedType.id
            }
            catch {
                Write-Error $_
                return
            }
        }

        # Fetch the delete preview to decide how to proceed and to obtain the exact type name for the
        # server's type-the-name confirmation.
        try {
            $impact = Invoke-JIMApi -Endpoint "/api/v1/metaverse/object-types/$Id/delete-preview"
        }
        catch {
            Write-Error "Failed to evaluate deletion of Metaverse Object Type ${Id}: $_"
            return
        }

        if ($impact.builtIn) {
            Write-Error "Cannot delete '$($impact.objectTypeName)': built-in Metaverse Object Types cannot be deleted."
            return
        }

        if ($impact.blockedByObjects) {
            Write-Error "Cannot delete '$($impact.objectTypeName)': $($impact.metaverseObjectCount) Metaverse Object(s) of this type exist. Delete the objects first, then retry."
            return
        }

        if ($impact.blockedBySynchronisationRules) {
            $ruleNames = ($impact.synchronisationRules | ForEach-Object { $_.description }) -join ', '
            Write-Error "Cannot delete '$($impact.objectTypeName)': $($impact.synchronisationRules.Count) Synchronisation Rule(s) target this type ($ruleNames). Remove those Synchronisation Rules first, then retry."
            return
        }

        $cascadeCount = if ($impact.cascadeReferences) { @($impact.cascadeReferences).Count } else { 0 }
        $action = if ($cascadeCount -gt 0) {
            "Delete Metaverse Object Type and cascade-remove $cascadeCount reference(s)"
        } else {
            "Delete Metaverse Object Type"
        }

        if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) {
            $ConfirmPreference = 'None'
        }

        if ($PSCmdlet.ShouldProcess($impact.objectTypeName, $action)) {
            Write-Verbose "Deleting Metaverse Object Type '$($impact.objectTypeName)'; references to cascade: $cascadeCount"

            try {
                # Always send confirmationName: the server requires it to match when references exist
                # and ignores it otherwise.
                $query = "confirmationName=$([uri]::EscapeDataString([string]$impact.objectTypeName))"
                if ($ChangeReason) {
                    $query += "&changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                $null = Invoke-JIMApi -Endpoint "/api/v1/metaverse/object-types/$Id`?$query" -Method 'DELETE'

                Write-Verbose "Deleted Metaverse Object Type '$($impact.objectTypeName)'"
            }
            catch {
                Write-Error "Failed to delete Metaverse Object Type '$($impact.objectTypeName)': $_"
            }
        }
    }
}
