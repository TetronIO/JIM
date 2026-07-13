# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    # Not connected is an expected precondition, not a failure. Report it as a
    # non-terminating error (matching every other cmdlet's guard) and return
    # nothing, rather than throwing: a raw throw makes ConciseView render this
    # helper's internal file and line, which reads like a crash. Callers can opt
    # into terminating behaviour with -ErrorAction Stop.
    if (-not $script:JIMConnection) {
        Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
        return
    }

    # Proactive token refresh: check before the request if token is near expiry
    if ($script:JIMConnection.AuthMethod -eq 'OAuth') {
        if ($script:JIMConnection.TokenExpiresAt -and $script:JIMConnection.TokenExpiresAt -lt (Get-Date).AddMinutes(2)) {
            Invoke-TokenRefresh -Reason "Access token expired or expiring soon"
        }
    }

    # Build and execute the request, with reactive 401 retry for OAuth
    $response = Invoke-JIMApiRequest -Endpoint $Endpoint -Method $Method -Body $Body -ContentType $ContentType

    # An empty response (a 204 No Content, or an empty JSON array that Invoke-RestMethod
    # enumerated into nothing) must stay empty. Passing it through the normaliser would
    # bind as $null and come back as an explicit $null OUTPUT ITEM, which callers using
    # @(Get-JIMXxx).Count would count as one object, breaking "is the list empty?" checks.
    if ($null -eq $response) {
        return
    }

    # Normalise the wire's camelCase property names to the PascalCase that PowerShell
    # cmdlet output is expected to use, at the single choke point every cmdlet funnels
    # through. Cmdlets read the result via case-insensitive member access, so their own
    # internal property reads (e.g. $response.items) are unaffected. Dynamic-key
    # dictionary values keep their keys verbatim (see ConvertTo-JIMOutputObject).
    #
    # Assign before returning: the normaliser wraps arrays with the comma operator to
    # protect nested single-element and empty arrays from being unrolled away. The
    # assignment collapses that protection at the top level, so a bare-array response
    # emits its elements individually here, exactly as the previous `return $response`
    # did. (A `return` of the call directly would keep the top-level array atomic.)
    $normalised = ConvertTo-JIMOutputObject -InputObject $response
    return $normalised
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

            # Write the rotated refresh token back to the credential store so the
            # persisted copy stays usable in future sessions (most IdPs rotate
            # refresh tokens on each use).
            if ($script:JIMConnection.Persisted) {
                try {
                    Save-JIMToken -BaseUrl $script:JIMConnection.Url -RefreshToken $tokens.RefreshToken | Out-Null
                }
                catch {
                    Write-Verbose "Failed to persist refreshed token: $_"
                }
            }
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

                # surface per-field validation errors (e.g. invalid connected system settings) so the caller sees
                # which fields failed and why, not just the summary message
                $validationErrors = $errorObj.validationErrors ?? $errorObj.ValidationErrors
                if ($validationErrors) {
                    $details = foreach ($field in $validationErrors.PSObject.Properties) {
                        foreach ($fieldMessage in $field.Value) {
                            "  - $($field.Name): $fieldMessage"
                        }
                    }
                    if ($details) {
                        $errorMessage = "$errorMessage`n$($details -join "`n")"
                    }
                }
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
                throw "Access denied. You are authenticated but not authorised to perform this operation. This usually means your identity has not been provisioned in JIM. Identities are created when you are synchronised into JIM from a connected system, or provisioned by an administrator. Contact your JIM administrator if this is unexpected."
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
