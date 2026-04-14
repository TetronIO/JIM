# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMPredefinedSearch {
    <#
    .SYNOPSIS
        Updates a Predefined Search in JIM.

    .DESCRIPTION
        Applies a partial update to a Predefined Search identified by its ID. Only the
        parameters that are explicitly provided are sent to the server; omitted fields
        are left unchanged.

        Disabled searches are hidden from the portal and from the end-user search API
        (Search-JIMMetaverseObject). They remain visible in the admin UI and to this cmdlet.

    .PARAMETER Id
        The unique identifier of the Predefined Search to update.

    .PARAMETER IsEnabled
        When specified, sets whether the search is available to end users. Pass $true to
        enable, $false to disable. Omit to leave the current state unchanged.

    .PARAMETER PassThru
        If specified, emits the updated Predefined Search header.

    .EXAMPLE
        Set-JIMPredefinedSearch -Id 3 -IsEnabled $false

        Disables the Predefined Search with ID 3.

    .EXAMPLE
        Get-JIMPredefinedSearch -Uri 'distribution-groups' | Set-JIMPredefinedSearch -IsEnabled $false

        Disables the 'distribution-groups' Predefined Search by piping its header in.

    .EXAMPLE
        Set-JIMPredefinedSearch -Id 3 -IsEnabled $true -PassThru

        Enables the search and returns the updated header.

    .LINK
        Get-JIMPredefinedSearch
        Search-JIMMetaverseObject
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter()]
        [bool]$IsEnabled,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Build a partial update body that only contains fields the caller explicitly bound.
        # Checking $PSBoundParameters distinguishes "-IsEnabled $false" (intentional) from
        # "-IsEnabled not provided" (leave unchanged) — [bool] alone cannot express this.
        $body = @{}
        if ($PSBoundParameters.ContainsKey('IsEnabled')) {
            $body.isEnabled = $IsEnabled
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if (-not $PSCmdlet.ShouldProcess("Predefined Search ID $Id", "Update")) {
            return
        }

        Write-Verbose "Updating Predefined Search: $Id"

        try {
            Invoke-JIMApi -Endpoint "/api/v1/predefined-searches/$Id" -Method 'PATCH' -Body $body | Out-Null
        }
        catch {
            Write-Error "Failed to update Predefined Search: $_"
            return
        }

        if ($PassThru) {
            Get-JIMPredefinedSearch -Id $Id
        }
    }
}
