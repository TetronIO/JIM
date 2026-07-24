# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Updates an existing Connected System in JIM.

    .DESCRIPTION
        Updates the name, description, setting values, export parallelism, and/or unresolved reference handling
        of an existing Connected System. Only the parameters provided will be updated.

    .PARAMETER Id
        The unique identifier of the Connected System to update.

    .PARAMETER InputObject
        Connected System object to update (from pipeline).

    .PARAMETER Name
        The new name for the Connected System.

    .PARAMETER Description
        The new description for the Connected System.

    .PARAMETER SettingValues
        A hashtable of setting values to update, where keys are setting IDs and values are
        hashtables with stringValue, intValue, or checkboxValue properties.

    .PARAMETER MaxExportParallelism
        Maximum number of export batches to process concurrently (1-16).
        Only applicable when the connector supports parallel export.
        Default is 1 (sequential processing).

    .PARAMETER UnresolvedReferenceHandling
        Controls how an import-time reference attribute value that cannot be resolved to a Connected System Object
        is treated: 'Error' (default; marks the affected Run Profile Execution Item as errored), 'Warn' (no
        per-object error, but the Activity completes with a warning summarising the count), or 'Ignore' (no
        per-object error and no Activity warning; unresolved references are still logged and remain visible on the
        Connected System Object).

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Connected System's change history.

    .PARAMETER PassThru
        If specified, returns the updated Connected System object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Connected System object.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -Name "Updated Name"

        Updates the name of the Connected System with ID 1.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -Description "New description" -PassThru

        Updates the description and returns the updated object.

    .EXAMPLE
        $settings = @{
            1 = @{ stringValue = "server.example.com" }
            2 = @{ intValue = 389 }
            3 = @{ checkboxValue = $true }
        }
        Set-JIMConnectedSystem -Id 1 -SettingValues $settings -ChangeReason "Point at new DC (CHG0099)"

        Updates multiple setting values for the Connected System and records a reason against its change history.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 | Set-JIMConnectedSystem -Name "Renamed"

        Updates a Connected System from the pipeline.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -MaxExportParallelism 4

        Enables parallel export batch processing with up to 4 concurrent batches.

    .EXAMPLE
        Set-JIMConnectedSystem -Id 1 -UnresolvedReferenceHandling Ignore

        Suppresses errors and warnings for import-time unresolved references (e.g. group members outside the
        configured Container Scope), while still logging each occurrence.

    .LINK
        Get-JIMConnectedSystem
        New-JIMConnectedSystem
        Remove-JIMConnectedSystem
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
        [string]$Description,

        [Parameter()]
        [hashtable]$SettingValues,

        [Parameter()]
        [ValidateRange(1, 16)]
        [int]$MaxExportParallelism,

        [Parameter()]
        [ValidateSet('Error', 'Warn', 'Ignore')]
        [string]$UnresolvedReferenceHandling,

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

        $systemId = if ($InputObject) { $InputObject.id } else { $Id }

        # Build update body
        $body = @{}

        if ($Name) {
            $body.name = $Name
        }

        if ($PSBoundParameters.ContainsKey('Description')) {
            $body.description = $Description
        }

        if ($SettingValues) {
            # Convert hashtable keys to strings for JSON serialization
            # JSON requires string keys, but PowerShell hashtables can have integer keys
            $stringKeyedSettings = @{}
            foreach ($key in $SettingValues.Keys) {
                $stringKeyedSettings[$key.ToString()] = $SettingValues[$key]
            }
            $body.settingValues = $stringKeyedSettings
        }

        if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
            $body.maxExportParallelism = $MaxExportParallelism
        }

        if ($PSBoundParameters.ContainsKey('UnresolvedReferenceHandling')) {
            $body.unresolvedReferenceHandling = $UnresolvedReferenceHandling
        }

        # A change reason alone is not an update; require at least one actual property change first.
        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSBoundParameters.ContainsKey('ChangeReason')) {
            $body.changeReason = $ChangeReason
        }

        $displayName = $Name ?? $systemId

        if ($PSCmdlet.ShouldProcess($displayName, "Update Connected System")) {
            Write-Verbose "Updating Connected System: $systemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Connected System: $systemId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Connected System: $_"
            }
        }
    }
}
