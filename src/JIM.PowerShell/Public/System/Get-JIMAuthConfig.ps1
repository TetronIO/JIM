# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMAuthConfig {
    <#
    .SYNOPSIS
        Retrieves the OIDC/OAuth client discovery configuration.

    .DESCRIPTION
        Calls the JIM auth config endpoint to retrieve the OIDC/OAuth configuration
        needed for client applications to initiate authentication. Useful for
        scripting SSO setup or validating configuration.

        This endpoint does not require authentication. Use -Url for standalone
        checks, or omit it to use the URL from an active Connect-JIM session.

    .PARAMETER Url
        The JIM instance's base URL, for example https://jim.example.com; the connected instance's when omitted.

    .OUTPUTS
        The settings a client needs to start a sign-in.

    .EXAMPLE
        Get-JIMAuthConfig -Url "https://jim.example.com"

    .EXAMPLE
        Get-JIMAuthConfig
    #>
    [CmdletBinding()]
    [OutputType('JIM.AuthConfigResponse')]
    param(
        [Parameter(Position = 0)]
        [string]$Url
    )

    process {
        $baseUrl = Resolve-JIMBaseUrl -Url $Url
        if (-not $baseUrl) { return }

        $uri = "$($baseUrl.TrimEnd('/'))/api/v1/auth/config"

        Write-Verbose "Getting auth config: $uri"

        try {
            $response = Invoke-RestMethod -Uri $uri -Method 'GET' -Headers @{ 'Accept' = 'application/json' } -ErrorAction Stop -MaximumRedirection 0
            $response
        }
        catch {
            Write-Error "Failed to get auth config from $uri`: $_"
        }
    }
}
