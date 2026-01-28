function Invoke-OAuthBrowserFlow {
    <#
    .SYNOPSIS
        Performs OAuth Authorization Code flow with PKCE using browser redirect.

    .DESCRIPTION
        This internal function handles interactive OAuth authentication:
        1. Generates PKCE code verifier and challenge
        2. Starts a local HTTP listener to receive the callback
        3. Opens the browser to the authorization endpoint
        4. Captures the authorization code from the callback
        5. Exchanges the code for tokens

        Security measures implemented per RFC 8252:
        - Binds to 127.0.0.1 loopback only (not 0.0.0.0)
        - Uses random ephemeral port if default is busy
        - Accepts exactly one request then shuts down
        - Validates state parameter for CSRF protection
        - Uses PKCE (code_challenge) to prevent authorization code interception

    .PARAMETER AuthorizeEndpoint
        The OAuth authorization endpoint URL.

    .PARAMETER TokenEndpoint
        The OAuth token endpoint URL.

    .PARAMETER ClientId
        The OAuth client ID.

    .PARAMETER Scopes
        Array of OAuth scopes to request.

    .PARAMETER RedirectPort
        The starting port to try for the callback. Defaults to 8400.
        Will try up to 10 ports if the specified port is in use.

    .PARAMETER TimeoutSeconds
        How long to wait for the user to complete authentication. Defaults to 300 (5 minutes).

    .OUTPUTS
        Returns a hashtable with AccessToken, RefreshToken, ExpiresAt, and TokenType.

    .NOTES
        This function implements:
        - PKCE (RFC 7636) as required by OAuth 2.1 for public clients
        - Loopback redirect security (RFC 8252 Section 7.3 and 8.3)
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [string]$AuthorizeEndpoint,

        [Parameter(Mandatory)]
        [string]$TokenEndpoint,

        [Parameter(Mandatory)]
        [string]$ClientId,

        [Parameter(Mandatory)]
        [string[]]$Scopes,

        [int]$RedirectPort = 8400,

        [int]$TimeoutSeconds = 300
    )

    # Generate PKCE code verifier (43-128 characters, unreserved URI characters)
    $codeVerifier = New-PkceCodeVerifier
    $codeChallenge = Get-PkceCodeChallenge -CodeVerifier $codeVerifier

    Write-Verbose "Generated PKCE code verifier and challenge"

    # Use a fixed callback path for IDP compatibility
    # Security is provided by PKCE + state parameter, not by path obscurity
    # Entra ID and other IDPs require exact redirect URI matching
    $callbackPath = "/callback/"

    # Find an available port on 127.0.0.1 loopback only (RFC 8252 Section 7.3)
    $listener = $null
    $actualPort = $RedirectPort
    $maxPortAttempts = 10

    for ($i = 0; $i -lt $maxPortAttempts; $i++) {
        try {
            $listener = [System.Net.HttpListener]::new()
            # Use localhost for IDP compatibility (Entra ID prefers http://localhost)
            # HttpListener on localhost still only accepts local connections
            $redirectUri = "http://localhost:$actualPort$callbackPath"
            $listener.Prefixes.Add($redirectUri)
            $listener.Start()
            Write-Verbose "Started HTTP listener on localhost:$actualPort with callback path $callbackPath"
            break
        }
        catch {
            if ($listener) {
                $listener.Close()
                $listener = $null
            }
            $actualPort++
            if ($i -eq $maxPortAttempts - 1) {
                throw "Failed to start HTTP listener. Tried ports $RedirectPort to $actualPort. Ensure no other application is using these ports."
            }
        }
    }

    try {
        $redirectUri = "http://localhost:$actualPort$callbackPath"

        # Generate cryptographically random state for CSRF protection
        $stateBytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        $state = [Convert]::ToBase64String($stateBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

        # Build authorization URL
        $authParams = @{
            client_id             = $ClientId
            response_type         = 'code'
            redirect_uri          = $redirectUri
            scope                 = $Scopes -join ' '
            state                 = $state
            code_challenge        = $codeChallenge
            code_challenge_method = 'S256'
        }

        $queryString = ($authParams.GetEnumerator() | ForEach-Object {
                "$([Uri]::EscapeDataString($_.Key))=$([Uri]::EscapeDataString($_.Value))"
            }) -join '&'

        $authUrl = "$AuthorizeEndpoint`?$queryString"

        Write-Verbose "Authorization URL: $authUrl"

        # Open browser
        Write-Host "Opening browser for authentication..." -ForegroundColor Cyan
        Write-Host "If the browser doesn't open automatically, navigate to:" -ForegroundColor Yellow
        Write-Host $authUrl -ForegroundColor Gray

        Open-Browser -Url $authUrl

        # Wait for callback (single request only per RFC 8252)
        Write-Host "Waiting for authentication to complete..." -ForegroundColor Cyan

        $context = $null
        $asyncResult = $listener.BeginGetContext($null, $null)

        if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutSeconds * 1000)) {
            throw "Authentication timed out after $TimeoutSeconds seconds. Please try again."
        }

        $context = $listener.EndGetContext($asyncResult)

        # Parse the callback
        $request = $context.Request
        $response = $context.Response

        # Verify the request path matches our callback path (defence in depth)
        # Normalise both paths by trimming trailing slashes for comparison
        $expectedPath = $callbackPath.TrimEnd('/')
        $actualPath = $request.Url.AbsolutePath.TrimEnd('/')
        if ($actualPath -ne $expectedPath) {
            Send-CallbackResponse -Response $response -Success $false -Message "Authentication failed: Invalid callback path"
            throw "Received callback on unexpected path. This could indicate a security issue."
        }

        $queryParams = [System.Web.HttpUtility]::ParseQueryString($request.Url.Query)

        # Check for errors
        if ($queryParams['error']) {
            $errorDescription = $queryParams['error_description'] ?? $queryParams['error']
            Send-CallbackResponse -Response $response -Success $false -Message "Authentication failed: $errorDescription"
            throw "OAuth error: $errorDescription"
        }

        # Validate state (CSRF protection)
        if ($queryParams['state'] -ne $state) {
            Send-CallbackResponse -Response $response -Success $false -Message "Authentication failed: Invalid state parameter"
            throw "OAuth state mismatch. This could indicate a CSRF attack. Please try again."
        }

        # Get authorization code
        $authCode = $queryParams['code']
        if (-not $authCode) {
            Send-CallbackResponse -Response $response -Success $false -Message "Authentication failed: No authorization code received"
            throw "No authorization code received from the identity provider."
        }

        # Send success response to browser
        Send-CallbackResponse -Response $response -Success $true -Message "Authentication successful! You can close this window."

        Write-Verbose "Received authorization code, exchanging for tokens..."

        # Exchange code for tokens
        $tokenParams = @{
            client_id     = $ClientId
            grant_type    = 'authorization_code'
            code          = $authCode
            redirect_uri  = $redirectUri
            code_verifier = $codeVerifier
        }

        $tokenBody = ($tokenParams.GetEnumerator() | ForEach-Object {
                "$([Uri]::EscapeDataString($_.Key))=$([Uri]::EscapeDataString($_.Value))"
            }) -join '&'

        $tokenResponse = Invoke-RestMethod -Uri $TokenEndpoint -Method Post -Body $tokenBody -ContentType 'application/x-www-form-urlencoded'

        # Calculate expiry time
        $expiresAt = (Get-Date).AddSeconds($tokenResponse.expires_in - 60)  # Subtract 60s buffer

        Write-Verbose "Successfully obtained access token"

        return @{
            AccessToken  = $tokenResponse.access_token
            RefreshToken = $tokenResponse.refresh_token
            ExpiresAt    = $expiresAt
            TokenType    = $tokenResponse.token_type ?? 'Bearer'
            Scopes       = $Scopes
        }
    }
    finally {
        # SECURITY: Always shut down listener after single request (RFC 8252)
        if ($listener) {
            $listener.Stop()
            $listener.Close()
            Write-Verbose "HTTP listener stopped"
        }
    }
}

function New-PkceCodeVerifier {
    <#
    .SYNOPSIS
        Generates a cryptographically random PKCE code verifier.

    .DESCRIPTION
        Creates a 64-character code verifier using unreserved URI characters
        as specified in RFC 7636.

    .OUTPUTS
        A 64-character string suitable for use as a PKCE code verifier.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    # Generate 48 random bytes (will produce 64 base64url characters)
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48)

    # Convert to base64url (RFC 4648)
    $base64 = [Convert]::ToBase64String($bytes)
    $base64Url = $base64.Replace('+', '-').Replace('/', '_').TrimEnd('=')

    return $base64Url
}

function Get-PkceCodeChallenge {
    <#
    .SYNOPSIS
        Generates a PKCE code challenge from a code verifier.

    .DESCRIPTION
        Creates a SHA256 hash of the code verifier and encodes it as base64url
        as specified in RFC 7636 for the S256 method.

    .PARAMETER CodeVerifier
        The PKCE code verifier to hash.

    .OUTPUTS
        The base64url-encoded SHA256 hash of the code verifier.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$CodeVerifier
    )

    $bytes = [System.Text.Encoding]::ASCII.GetBytes($CodeVerifier)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)

    # Convert to base64url (RFC 4648)
    $base64 = [Convert]::ToBase64String($hash)
    $base64Url = $base64.Replace('+', '-').Replace('/', '_').TrimEnd('=')

    return $base64Url
}

function Open-Browser {
    <#
    .SYNOPSIS
        Opens the default browser to a specified URL.

    .DESCRIPTION
        Cross-platform function to open a URL in the default browser.
        Supports Windows, macOS, and Linux.

    .PARAMETER Url
        The URL to open.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Url
    )

    try {
        if ($IsWindows -or $env:OS -match 'Windows') {
            # Windows
            Start-Process $Url
        }
        elseif ($IsMacOS) {
            # macOS
            Start-Process 'open' -ArgumentList $Url
        }
        else {
            # Linux - try common browsers/openers
            $browsers = @('xdg-open', 'sensible-browser', 'x-www-browser', 'gnome-open', 'firefox', 'chromium-browser', 'google-chrome')
            $opened = $false

            foreach ($browser in $browsers) {
                try {
                    $null = Get-Command $browser -ErrorAction Stop
                    Start-Process $browser -ArgumentList $Url
                    $opened = $true
                    break
                }
                catch {
                    continue
                }
            }

            if (-not $opened) {
                Write-Warning "Could not open browser automatically. Please open the URL manually."
            }
        }
    }
    catch {
        Write-Warning "Failed to open browser: $_"
        Write-Warning "Please open the URL manually."
    }
}

function Send-CallbackResponse {
    <#
    .SYNOPSIS
        Sends an HTML response to the OAuth callback request.

    .DESCRIPTION
        Sends a simple HTML page to the browser indicating success or failure.

    .PARAMETER Response
        The HttpListenerResponse object.

    .PARAMETER Success
        Whether authentication was successful.

    .PARAMETER Message
        The message to display.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Net.HttpListenerResponse]$Response,

        [Parameter(Mandatory)]
        [bool]$Success,

        [Parameter(Mandatory)]
        [string]$Message
    )

    # JIM brand colours (matches JIM.Web dark theme)
    $bgPage = '#0e1420'
    $bgCard = '#121826'
    $borderColour = '#2a303c'
    $accentColour = if ($Success) { '#7c4dff' } else { '#f44336' }
    $textColour = '#ffffffde'
    $subtitleColour = '#ffffff99'
    $icon = if ($Success) { '&#10003;' } else { '&#10007;' }

    $html = @"
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <title>JIM Authentication</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            background: $bgPage;
        }
        .container {
            text-align: center;
            padding: 40px 60px;
            background: $bgCard;
            border: 1px solid $borderColour;
            border-radius: 12px;
            max-width: 400px;
            box-shadow: 0 4px 24px rgba(124, 77, 255, 0.15);
        }
        .icon {
            font-size: 48px;
            color: $accentColour;
            margin-bottom: 16px;
        }
        .message {
            font-size: 18px;
            color: $textColour;
            margin-bottom: 8px;
            font-weight: 500;
        }
        .subtitle {
            font-size: 14px;
            color: $subtitleColour;
        }
        .logo {
            font-size: 24px;
            font-weight: 700;
            color: $accentColour;
            margin-bottom: 24px;
            letter-spacing: 2px;
        }
    </style>
</head>
<body>
    <div class="container">
        <div class="logo">JIM</div>
        <div class="icon">$icon</div>
        <div class="message">$Message</div>
        <div class="subtitle">You can close this window and return to PowerShell.</div>
    </div>
</body>
</html>
"@

    $buffer = [System.Text.Encoding]::UTF8.GetBytes($html)
    $Response.ContentLength64 = $buffer.Length
    $Response.ContentType = 'text/html; charset=utf-8'
    $Response.StatusCode = 200
    $Response.OutputStream.Write($buffer, 0, $buffer.Length)
    $Response.OutputStream.Close()
}

function Invoke-OAuthTokenRefresh {
    <#
    .SYNOPSIS
        Refreshes an OAuth access token using a refresh token.

    .DESCRIPTION
        Uses the refresh token to obtain a new access token without requiring
        user interaction.

    .PARAMETER TokenEndpoint
        The OAuth token endpoint URL.

    .PARAMETER ClientId
        The OAuth client ID.

    .PARAMETER RefreshToken
        The refresh token to use.

    .PARAMETER Scopes
        Array of OAuth scopes to request.

    .OUTPUTS
        Returns a hashtable with AccessToken, RefreshToken, ExpiresAt, and TokenType.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [string]$TokenEndpoint,

        [Parameter(Mandatory)]
        [string]$ClientId,

        [Parameter(Mandatory)]
        [string]$RefreshToken,

        [Parameter(Mandatory)]
        [string[]]$Scopes
    )

    Write-Verbose "Refreshing access token..."

    $tokenParams = @{
        client_id     = $ClientId
        grant_type    = 'refresh_token'
        refresh_token = $RefreshToken
        scope         = $Scopes -join ' '
    }

    $tokenBody = ($tokenParams.GetEnumerator() | ForEach-Object {
            "$([Uri]::EscapeDataString($_.Key))=$([Uri]::EscapeDataString($_.Value))"
        }) -join '&'

    try {
        $tokenResponse = Invoke-RestMethod -Uri $TokenEndpoint -Method Post -Body $tokenBody -ContentType 'application/x-www-form-urlencoded'

        # Calculate expiry time
        $expiresAt = (Get-Date).AddSeconds($tokenResponse.expires_in - 60)

        Write-Verbose "Successfully refreshed access token"

        return @{
            AccessToken  = $tokenResponse.access_token
            RefreshToken = $tokenResponse.refresh_token ?? $RefreshToken  # Some providers don't return a new refresh token
            ExpiresAt    = $expiresAt
            TokenType    = $tokenResponse.token_type ?? 'Bearer'
            Scopes       = $Scopes
        }
    }
    catch {
        Write-Verbose "Token refresh failed: $_"
        throw "Failed to refresh access token. You may need to re-authenticate using Connect-JIM."
    }
}

function Get-OidcDiscoveryDocument {
    <#
    .SYNOPSIS
        Fetches the OIDC discovery document from an authority.

    .DESCRIPTION
        Retrieves the OpenID Connect discovery document to obtain
        the authorization and token endpoints.

    .PARAMETER Authority
        The OIDC authority URL.

    .OUTPUTS
        Returns a hashtable with AuthorizeEndpoint and TokenEndpoint.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [string]$Authority
    )

    $discoveryUrl = "$($Authority.TrimEnd('/'))/.well-known/openid-configuration"
    Write-Verbose "Fetching OIDC discovery document from $discoveryUrl"

    try {
        $discovery = Invoke-RestMethod -Uri $discoveryUrl -Method Get

        return @{
            AuthorizeEndpoint = $discovery.authorization_endpoint
            TokenEndpoint     = $discovery.token_endpoint
            Issuer            = $discovery.issuer
        }
    }
    catch {
        throw "Failed to fetch OIDC discovery document from $discoveryUrl`: $_"
    }
}
