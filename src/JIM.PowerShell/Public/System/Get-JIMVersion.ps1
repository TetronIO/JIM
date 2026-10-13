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
        The JIM instance's base URL, for example https://jim.example.com; the connected instance's when omitted.

    .OUTPUTS
        One object with Product and Version.

    .EXAMPLE
        Get-JIMVersion -Url "https://jim.example.com"

    .EXAMPLE
        Get-JIMVersion
    #>
    [CmdletBinding()]
    [OutputType('JIM.VersionResponse')]
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
