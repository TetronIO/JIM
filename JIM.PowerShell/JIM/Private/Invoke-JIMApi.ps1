function Invoke-JIMApi {
    <#
    .SYNOPSIS
        Internal function to invoke JIM REST API endpoints.

    .DESCRIPTION
        This is a private helper function that handles all REST API calls to JIM.
        It manages authentication headers (API key or Bearer token), error handling,
        and response processing.

        Supports automatic token refresh for OAuth connections - both proactively
        (before the request, when the token is near expiry) and reactively (on 401
        response, in case of clock skew or server-side revocation).

    .PARAMETER Endpoint
        The API endpoint path (without base URL), e.g., '/api/v1/synchronisation/connected-systems'

    .PARAMETER Method
        The HTTP method to use. Defaults to 'GET'.

    .PARAMETER Body
        Optional body for POST/PUT/PATCH requests. Will be converted to JSON.

    .PARAMETER ContentType
        Content type for the request. Defaults to 'application/json'.

    .OUTPUTS
        The API response object, or throws an error if the request fails.

    .EXAMPLE
        Invoke-JIMApi -Endpoint '/api/v1/synchronisation/connected-systems'

    .EXAMPLE
        Invoke-JIMApi -Endpoint '/api/v1/synchronisation/connected-systems' -Method 'POST' -Body @{ Name = 'Test' }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Endpoint,

        [ValidateSet('GET', 'POST', 'PUT', 'PATCH', 'DELETE')]
        [string]$Method = 'GET',

        [object]$Body,

        [string]$ContentType = 'application/json'
    )

    # Check connection
    if (-not $script:JIMConnection) {
        throw "Not connected to JIM. Use Connect-JIM first."
    }

    # Proactive token refresh: check before the request if token is near expiry
    if ($script:JIMConnection.AuthMethod -eq 'OAuth') {
        if ($script:JIMConnection.TokenExpiresAt -and $script:JIMConnection.TokenExpiresAt -lt (Get-Date).AddMinutes(2)) {
            Invoke-TokenRefresh -Reason "Access token expired or expiring soon"
        }
    }

    # Build and execute the request, with reactive 401 retry for OAuth
    $response = Invoke-JIMApiRequest -Endpoint $Endpoint -Method $Method -Body $Body -ContentType $ContentType

    return $response
}

function Invoke-TokenRefresh {
    <#
    .SYNOPSIS
        Refreshes the OAuth access token using the stored refresh token.
    #>
    [CmdletBinding()]
    param(
        [string]$Reason = "Token refresh required"
    )

    if ($script:JIMConnection.RefreshToken -and $script:JIMConnection.OAuthConfig) {
        try {
            Write-Verbose "$Reason, refreshing..."
            $tokens = Invoke-OAuthTokenRefresh `
                -TokenEndpoint $script:JIMConnection.OAuthConfig.TokenEndpoint `
                -ClientId $script:JIMConnection.OAuthConfig.ClientId `
                -RefreshToken $script:JIMConnection.RefreshToken `
                -Scopes $script:JIMConnection.OAuthConfig.Scopes

            $script:JIMConnection.AccessToken = $tokens.AccessToken
            $script:JIMConnection.RefreshToken = $tokens.RefreshToken
            $script:JIMConnection.TokenExpiresAt = $tokens.ExpiresAt
            Write-Verbose "Successfully refreshed access token"
        }
        catch {
            throw "Access token expired and refresh failed. Please run Connect-JIM again to re-authenticate. Error: $_"
        }
    }
    else {
        throw "Access token expired and no refresh token available. Please run Connect-JIM again to re-authenticate."
    }
}

function Invoke-JIMApiRequest {
    <#
    .SYNOPSIS
        Executes a single API request with authentication and error handling.
        For OAuth connections, automatically retries once on 401 after refreshing the token.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Endpoint,

        [string]$Method = 'GET',

        [object]$Body,

        [string]$ContentType = 'application/json',

        [switch]$IsRetry
    )

    # Build the full URI
    $uri = "$($script:JIMConnection.Url.TrimEnd('/'))$Endpoint"

    Write-Debug "Invoking JIM API: $Method $uri"

    # Build request parameters with appropriate authentication header
    $headers = @{
        'Accept' = 'application/json'
    }

    if ($script:JIMConnection.AuthMethod -eq 'ApiKey') {
        $headers['X-API-Key'] = $script:JIMConnection.ApiKey
    }
    elseif ($script:JIMConnection.AuthMethod -eq 'OAuth') {
        $headers['Authorization'] = "Bearer $($script:JIMConnection.AccessToken)"
    }
    else {
        throw "Unknown authentication method: $($script:JIMConnection.AuthMethod)"
    }

    $params = @{
        Uri         = $uri
        Method      = $Method
        ContentType = $ContentType
        Headers     = $headers
    }

    # Add body if provided
    if ($Body) {
        if ($Body -is [string]) {
            $params.Body = $Body
        }
        else {
            $params.Body = $Body | ConvertTo-Json -Depth 10
        }
        Write-Debug "Request body: $($params.Body)"
    }

    try {
        $response = Invoke-RestMethod @params -ErrorAction Stop -MaximumRedirection 0
        Write-Debug "API response received successfully"
        return $response
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        $errorMessage = $_.ErrorDetails.Message

        if ($errorMessage) {
            try {
                $errorObj = $errorMessage | ConvertFrom-Json
                $errorMessage = $errorObj.message ?? $errorObj.Message ?? $errorMessage
            }
            catch {
                # Keep original error message if JSON parsing fails
            }
        }

        switch ($statusCode) {
            401 {
                # For OAuth: attempt a reactive token refresh and retry once
                if ($script:JIMConnection.AuthMethod -eq 'OAuth' -and -not $IsRetry) {
                    try {
                        Invoke-TokenRefresh -Reason "Server rejected token (401), attempting refresh"
                        # Retry the request once with the new token
                        return Invoke-JIMApiRequest -Endpoint $Endpoint -Method $Method -Body $Body -ContentType $ContentType -IsRetry
                    }
                    catch {
                        throw "Authentication failed after token refresh. Please run Connect-JIM to re-authenticate. Error: $_"
                    }
                }
                elseif ($script:JIMConnection.AuthMethod -eq 'OAuth') {
                    throw "Authentication failed. Token refresh was already attempted. Please run Connect-JIM to re-authenticate."
                }
                else {
                    throw "Authentication failed. Your API key may be invalid or expired. Use Connect-JIM to reconnect."
                }
            }
            403 {
                throw "Access denied. You do not have permission to perform this operation."
            }
            404 {
                throw "Resource not found: $errorMessage"
            }
            default {
                throw "JIM API error ($statusCode): $errorMessage"
            }
        }
    }
}
