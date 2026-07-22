# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Test-JIMMetaverseAttributeName {
    <#
    .SYNOPSIS
        Tests whether a Metaverse Attribute name is available.

    .DESCRIPTION
        Checks whether a name is free to use for a custom Metaverse Attribute. The comparison is
        case-insensitive, so "CostCentre" is reported as taken if "costCentre" already exists.
        Returns $true when the name is available, $false when it is already in use.

        When renaming an existing attribute, pass its ID to -ExcludeId so the attribute's own name
        does not count as a clash.

    .PARAMETER Name
        The attribute name to test.

    .PARAMETER ExcludeId
        Optional ID of an existing attribute to exclude from the check (use when renaming, so the
        attribute's current name is not treated as a clash with itself).

    .OUTPUTS
        [bool] $true if the name is available, otherwise $false.

    .EXAMPLE
        Test-JIMMetaverseAttributeName -Name "CostCentre"

        Returns $true if no attribute already uses that name (case-insensitively).

    .EXAMPLE
        if (Test-JIMMetaverseAttributeName -Name "CostCentre") { New-JIMMetaverseAttribute -Name "CostCentre" -Type Text }

        Guards a create call with an availability check.

    .EXAMPLE
        Test-JIMMetaverseAttributeName -Name "CostCentre" -ExcludeId 42

        Checks availability while renaming attribute 42, ignoring its own current name.

    .LINK
        New-JIMMetaverseAttribute
        Set-JIMMetaverseAttribute
        Get-JIMMetaverseAttribute
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [int]$ExcludeId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        try {
            $endpoint = "/api/v1/metaverse/attributes/name-availability?name=$([uri]::EscapeDataString($Name))"
            if ($PSBoundParameters.ContainsKey('ExcludeId')) {
                $endpoint += "&excludeId=$ExcludeId"
            }
            $result = Invoke-JIMApi -Endpoint $endpoint

            [bool]$result.available
        }
        catch {
            Write-Error "Failed to check availability of Metaverse Attribute name '$Name': $_"
        }
    }
}
