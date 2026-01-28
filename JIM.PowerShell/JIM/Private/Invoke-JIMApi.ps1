function Invoke-JIMApi {
    <#
    .SYNOPSIS
        Internal function to invoke JIM REST API endpoints.

    .DESCRIPTION
        This is a private helper function that handles all REST API calls to JIM.
        It manages authentication headers (API key or Bearer token), error handling,
        and response processing.

        Supports automatic token refresh for OAuth connections.

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

    # Check if we need to refresh the OAuth token
    if ($script:JIMConnection.AuthMethod -eq 'OAuth') {
        if ($script:JIMConnection.TokenExpiresAt -and $script:JIMConnection.TokenExpiresAt -lt (Get-Date).AddMinutes(2)) {
            # Token is expired or about to expire, try to refresh
            if ($script:JIMConnection.RefreshToken -and $script:JIMConnection.OAuthConfig) {
                try {
                    Write-Verbose "Access token expired or expiring soon, refreshing..."
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
                    throw "Access token expired and refresh failed. Please run Connect-JIM again to re-authenticate."
                }
            }
            else {
                throw "Access token expired. Please run Connect-JIM again to re-authenticate."
            }
        }
    }

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
                if ($script:JIMConnection.AuthMethod -eq 'OAuth') {
                    throw "Authentication failed. Your session may have expired. Use Connect-JIM to reconnect."
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
