# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMVersion {
    <#
    .SYNOPSIS
        Retrieves the JIM application version.

    .DESCRIPTION
        Calls the JIM version endpoint to retrieve the product name and version
        number. This endpoint does not require authentication.

        Use -Url for standalone checks, or omit it to use the URL from an active
        Connect-JIM session.

    .PARAMETER Url
        Base URL of the JIM instance, e.g. "https://jim.example.com".
        If omitted, uses the URL from the current Connect-JIM session.

    .OUTPUTS
        PSCustomObject with product and version properties.

    .EXAMPLE
        Get-JIMVersion -Url "https://jim.example.com"

    .EXAMPLE
        Get-JIMVersion
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Position = 0)]
        [string]$Url
    )

    process {
        $baseUrl = Resolve-JIMBaseUrl -Url $Url
        if (-not $baseUrl) { return }

        $uri = "$($baseUrl.TrimEnd('/'))/api/v1/health/version"

        Write-Verbose "Getting version: $uri"

        try {
            $response = Invoke-RestMethod -Uri $uri -Method 'GET' -Headers @{ 'Accept' = 'application/json' } -ErrorAction Stop -MaximumRedirection 0
            $response
        }
        catch {
            Write-Error "Failed to get version from $uri`: $_"
        }
    }
}
