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
        Base URL of the JIM instance, e.g. "https://jim.example.com".
        If omitted, uses the URL from the current Connect-JIM session.

    .OUTPUTS
        PSCustomObject with OIDC configuration properties including authority,
        clientId, scopes, responseType, usePkce, and codeChallengeMethod.

    .EXAMPLE
        Get-JIMAuthConfig -Url "https://jim.example.com"

    .EXAMPLE
        Get-JIMAuthConfig
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
