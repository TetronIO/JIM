# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Connect-JIM {
    <#
    .SYNOPSIS
        Connects to a JIM instance for administration.

    .DESCRIPTION
        Establishes a connection to a JIM (Junctional Identity Manager) instance.
        This connection is required before using any other JIM cmdlets.

        Supports two authentication methods:
        1. Interactive browser-based SSO authentication (default for interactive sessions)
        2. API key authentication (for automation, CI/CD, and scripting)

        For interactive (SSO) sign-ins, the refresh token is persisted in the operating
        system's credential store (Credential Manager on Windows, login Keychain on macOS,
        libsecret on Linux) so that opening a new terminal reconnects silently without a
        browser. Only the refresh token is stored, never the access token. On systems with
        no usable credential store (typically headless Linux without a keyring), the module
        falls back to in-memory tokens for the session. Use -NoPersist to opt out.

    .PARAMETER Url
        The base URL of the JIM instance, e.g., 'https://jim.company.com' or 'http://localhost:5200'.

    .PARAMETER ApiKey
        The API key for authentication. API keys can be created in the JIM web interface
        under Admin > API Keys. When specified, skips interactive authentication. API key
        connections never read or write the credential store.

    .PARAMETER Force
        Forces re-authentication even if a valid session exists. Ignores any persisted
        refresh token and overwrites it with the newly obtained one.

    .PARAMETER NoPersist
        Authenticates for this session only, without reading from or writing to the
        operating system credential store. Useful on shared machines.

    .PARAMETER TimeoutSeconds
        How long to wait for interactive authentication to complete. Defaults to 300 (5 minutes).

    .OUTPUTS
        Returns the connection information on success.

    .EXAMPLE
        Connect-JIM -Url "https://jim.company.com"

        Connects using interactive browser-based SSO authentication.
        Opens the default browser for authentication.

    .EXAMPLE
        Connect-JIM -Url "https://jim.company.com" -ApiKey "jim_ak_abc123..."

        Connects using an API key (for automation scenarios).

    .EXAMPLE
        Connect-JIM -Url "http://localhost:5200" -ApiKey $env:JIM_API_KEY

        Connects to a local JIM instance using an API key from an environment variable.

    .EXAMPLE
        Connect-JIM -Url "https://jim.company.com" -Force

        Forces re-authentication, ignoring any cached session.

    .EXAMPLE
        Connect-JIM -Url "https://jim.company.com" -NoPersist

        Connects interactively without persisting the refresh token to the OS
        credential store (in-memory for this session only).

    .NOTES
        Interactive authentication requires:
        - SSO to be configured on the JIM server
        - A browser to be available
        - The IDP to have localhost redirect URIs configured

        For automation scenarios (CI/CD, scripts), use API key authentication.
        API keys can be created in the JIM web interface under Admin > API Keys.

    .LINK
        Disconnect-JIM
        Test-JIMConnection
        https://github.com/TetronIO/JIM
    #>
    [CmdletBinding(DefaultParameterSetName = 'Interactive')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Url,

        [Parameter(Mandatory, ParameterSetName = 'ApiKey', Position = 1)]
        [ValidateNotNullOrEmpty()]
        [string]$ApiKey,

        [Parameter(ParameterSetName = 'Interactive')]
        [switch]$Force,

        [Parameter(ParameterSetName = 'Interactive')]
        [switch]$NoPersist,

        [Parameter(ParameterSetName = 'Interactive')]
        [ValidateRange(30, 600)]
        [int]$TimeoutSeconds = 300
    )

    Write-Verbose "Connecting to JIM at $Url"

    # Validate URL format
    if (-not ($Url -match '^https?://')) {
        throw "Invalid URL format. URL must start with http:// or https://"
    }

    $baseUrl = $Url.TrimEnd('/')

    # Check if we should use API key authentication
    if ($PSCmdlet.ParameterSetName -eq 'ApiKey') {
        return Connect-JIMWithApiKey -BaseUrl $baseUrl -ApiKey $ApiKey
    }

    # Interactive authentication
    return Connect-JIMInteractive -BaseUrl $baseUrl -Force:$Force -NoPersist:$NoPersist -TimeoutSeconds $TimeoutSeconds
}

function Connect-JIMWithApiKey {
    <#
    .SYNOPSIS
        Internal function to connect using API key authentication.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$BaseUrl,

        [Parameter(Mandatory)]
        [string]$ApiKey
    )

    # Store connection info
    $script:JIMConnection = [PSCustomObject]@{
        Url            = $BaseUrl
        ApiKey         = $ApiKey
        AccessToken    = $null
        RefreshToken   = $null
        TokenExpiresAt = $null
        AuthMethod     = 'ApiKey'
        Connected      = $false
    }

    # Test the connection
    try {
        Write-Verbose "Testing connection to JIM..."
        $health = Invoke-JIMApi -Endpoint '/api/v1/health'

        $script:JIMConnection.Connected = $true

        # Fetch server version
        $serverVersion = Get-JIMServerVersion

        Write-Verbose "Successfully connected to JIM using API key"

        Show-JIMBanner -ServerVersion $serverVersion -Url $BaseUrl

        # Note: Skip authorisation check for API keys - they are authorised by definition
        # (they have explicit roles assigned at creation time). The userinfo endpoint checks
        # for a MetaverseObject which only applies to interactive (SSO) users.

        # Return connection info (without exposing full API key)
        $keyPreview = if ($ApiKey.Length -gt 12) {
            $ApiKey.Substring(0, 8) + "..." + $ApiKey.Substring($ApiKey.Length - 4)
        }
        else {
            "***"
        }

        [PSCustomObject]@{
            Url           = $script:JIMConnection.Url
            AuthMethod    = 'ApiKey'
            ApiKey        = $keyPreview
            Connected     = $true
            ServerVersion = $serverVersion
            Authorised    = $true
            Status        = $health.status ?? 'Connected'
        }
    }
    catch {
        $script:JIMConnection = $null
        throw "Failed to connect to JIM at $BaseUrl`: $_"
    }
}

function Connect-JIMInteractive {
    <#
    .SYNOPSIS
        Internal function to connect using interactive browser authentication.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$BaseUrl,

        [switch]$Force,

        [switch]$NoPersist,

        [int]$TimeoutSeconds = 300
    )

    # Check for existing valid session
    if (-not $Force -and $script:JIMConnection -and $script:JIMConnection.AuthMethod -eq 'OAuth') {
        if ($script:JIMConnection.Url -eq $BaseUrl -and $script:JIMConnection.Connected) {
            # Check if token is still valid (with 5 minute buffer)
            if ($script:JIMConnection.TokenExpiresAt -and $script:JIMConnection.TokenExpiresAt -gt (Get-Date).AddMinutes(5)) {
                Write-Verbose "Using existing valid OAuth session"
                return [PSCustomObject]@{
                    Url        = $script:JIMConnection.Url
                    AuthMethod = 'OAuth'
                    Connected  = $true
                    ExpiresAt  = $script:JIMConnection.TokenExpiresAt
                    Status     = 'Connected (cached)'
                }
            }

            # Try to refresh the token
            if ($script:JIMConnection.RefreshToken -and $script:JIMConnection.OAuthConfig) {
                try {
                    Write-Verbose "Access token expired, attempting refresh..."
                    $tokens = Invoke-OAuthTokenRefresh `
                        -TokenEndpoint $script:JIMConnection.OAuthConfig.TokenEndpoint `
                        -ClientId $script:JIMConnection.OAuthConfig.ClientId `
                        -RefreshToken $script:JIMConnection.RefreshToken `
                        -Scopes $script:JIMConnection.OAuthConfig.Scopes

                    $script:JIMConnection.AccessToken = $tokens.AccessToken
                    $script:JIMConnection.RefreshToken = $tokens.RefreshToken
                    $script:JIMConnection.TokenExpiresAt = $tokens.ExpiresAt

                    # Write the rotated refresh token back to the credential store so the
                    # persisted copy stays valid (most IdPs rotate refresh tokens on use).
                    if ($script:JIMConnection.Persisted) {
                        try {
                            Save-JIMToken -BaseUrl $script:JIMConnection.Url -RefreshToken $tokens.RefreshToken | Out-Null
                        }
                        catch {
                            Write-Verbose "Failed to persist refreshed token: $_"
                        }
                    }

                    Write-Verbose "Successfully refreshed access token"
                    $serverVersion = Get-JIMServerVersion
                    Show-JIMBanner -ServerVersion $serverVersion -Url $script:JIMConnection.Url
                    return [PSCustomObject]@{
                        Url           = $script:JIMConnection.Url
                        AuthMethod    = 'OAuth'
                        Connected     = $true
                        ServerVersion = $serverVersion
                        ExpiresAt     = $tokens.ExpiresAt
                        Status        = 'Connected (refreshed)'
                    }
                }
                catch {
                    Write-Verbose "Token refresh failed, proceeding with full authentication: $_"
                }
            }
        }
    }

    # Get OAuth configuration from JIM
    Write-Verbose "Fetching OAuth configuration from JIM..."
    try {
        $authConfig = Invoke-RestMethod -Uri "$BaseUrl/api/v1/auth/config" -Method Get
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 503) {
            throw "SSO is not configured on this JIM instance. Use Connect-JIM with -ApiKey parameter for API key authentication."
        }
        throw "Failed to get OAuth configuration from JIM: $_"
    }

    # Get OIDC discovery document
    Write-Verbose "Fetching OIDC discovery document from $($authConfig.authority)..."
    $discovery = Get-OidcDiscoveryDocument -Authority $authConfig.authority

    # Determine whether persistent token storage is in play for this session.
    # -NoPersist opts out; otherwise it depends on whether the OS has a usable
    # credential store (Windows/macOS always; Linux only with libsecret present).
    $persistenceAvailable = Test-JIMTokenPersistenceAvailable
    $usePersistence = (-not $NoPersist) -and $persistenceAvailable

    # Attempt a silent reconnect using a persisted refresh token before opening a
    # browser. Skipped when -Force is set (force always re-authenticates and then
    # overwrites the stored token).
    if ($usePersistence -and -not $Force) {
        $cachedRefreshToken = Get-JIMPersistedToken -BaseUrl $BaseUrl
        if ($cachedRefreshToken) {
            try {
                Write-Verbose "Found a persisted refresh token; attempting silent reconnect..."
                $tokens = Invoke-OAuthTokenRefresh `
                    -TokenEndpoint $discovery.TokenEndpoint `
                    -ClientId $authConfig.clientId `
                    -RefreshToken $cachedRefreshToken `
                    -Scopes $authConfig.scopes

                $script:JIMConnection = [PSCustomObject]@{
                    Url            = $BaseUrl
                    ApiKey         = $null
                    AccessToken    = $tokens.AccessToken
                    RefreshToken   = $tokens.RefreshToken
                    TokenExpiresAt = $tokens.ExpiresAt
                    AuthMethod     = 'OAuth'
                    Connected      = $false
                    Persisted      = $true
                    OAuthConfig    = @{
                        Authority     = $authConfig.authority
                        ClientId      = $authConfig.clientId
                        Scopes        = $authConfig.scopes
                        TokenEndpoint = $discovery.TokenEndpoint
                    }
                }

                Write-Verbose "Testing connection to JIM with cached credentials..."
                $health = Invoke-JIMApi -Endpoint '/api/v1/health'
                $script:JIMConnection.Connected = $true

                $serverVersion = Get-JIMServerVersion
                Show-JIMBanner -ServerVersion $serverVersion -Url $BaseUrl -StatusLine "Connected to JIM using cached credentials (no browser sign-in required)."
                $userInfo = Test-JIMAuthorisation

                # Persist the (possibly rotated) refresh token.
                try {
                    Save-JIMToken -BaseUrl $BaseUrl -RefreshToken $tokens.RefreshToken | Out-Null
                }
                catch {
                    Write-Verbose "Failed to persist refreshed token: $_"
                }

                return [PSCustomObject]@{
                    Url           = $script:JIMConnection.Url
                    AuthMethod    = 'OAuth'
                    Connected     = $true
                    ServerVersion = $serverVersion
                    ExpiresAt     = $tokens.ExpiresAt
                    Authorised    = $userInfo.authorised ?? $null
                    Status        = 'Connected (cached)'
                }
            }
            catch {
                Write-Verbose "Silent reconnect with persisted token failed, falling back to browser sign-in: $_"
                $script:JIMConnection = $null
                # Drop the stale token so we don't keep retrying a dead refresh token.
                try {
                    Remove-JIMToken -BaseUrl $BaseUrl | Out-Null
                }
                catch {
                    Write-Verbose "Failed to remove stale persisted token: $_"
                }
            }
        }
    }

    # Tell the user when persistence was wanted but the platform has no usable store
    # (typically headless/SSH Linux without a keyring), and point them at -ApiKey.
    if (-not $NoPersist -and -not $persistenceAvailable) {
        Write-Host "Token persistence is unavailable on this system (no OS keyring detected); you will need to re-authenticate in new sessions. For unattended or headless use, connect with -ApiKey instead." -ForegroundColor DarkGray
    }

    # Perform browser-based authentication
    Write-Host ""
    Write-Host "Starting interactive authentication with JIM..." -ForegroundColor Cyan
    Write-Host "You will be redirected to your organisation's identity provider." -ForegroundColor Gray
    Write-Host ""

    $tokens = Invoke-OAuthBrowserFlow `
        -AuthorizeEndpoint $discovery.AuthorizeEndpoint `
        -TokenEndpoint $discovery.TokenEndpoint `
        -ClientId $authConfig.clientId `
        -Scopes $authConfig.scopes `
        -TimeoutSeconds $TimeoutSeconds

    # Store connection info
    $script:JIMConnection = [PSCustomObject]@{
        Url            = $BaseUrl
        ApiKey         = $null
        AccessToken    = $tokens.AccessToken
        RefreshToken   = $tokens.RefreshToken
        TokenExpiresAt = $tokens.ExpiresAt
        AuthMethod     = 'OAuth'
        Connected      = $false
        Persisted      = $usePersistence
        OAuthConfig    = @{
            Authority     = $authConfig.authority
            ClientId      = $authConfig.clientId
            Scopes        = $authConfig.scopes
            TokenEndpoint = $discovery.TokenEndpoint
        }
    }

    # Test the connection with the new token
    try {
        Write-Verbose "Testing connection to JIM with OAuth token..."
        $health = Invoke-JIMApi -Endpoint '/api/v1/health'

        $script:JIMConnection.Connected = $true

        # Fetch server version
        $serverVersion = Get-JIMServerVersion

        # When the user opted out of persistence, confirm it directly under the
        # connected line so it is obvious a new terminal will need to sign in again.
        $bannerStatus = @()
        if ($NoPersist) {
            $bannerStatus += "Auth persistence disabled (-NoPersist): a new terminal will require a fresh sign-in."
        }
        Show-JIMBanner -ServerVersion $serverVersion -Url $BaseUrl -StatusLine $bannerStatus

        # Verify the user is authorised to use JIM
        $userInfo = Test-JIMAuthorisation

        # Persist the refresh token so future sessions can reconnect without a browser.
        if ($usePersistence -and $tokens.RefreshToken) {
            try {
                Save-JIMToken -BaseUrl $BaseUrl -RefreshToken $tokens.RefreshToken | Out-Null
                Write-Verbose "Persisted refresh token for future sessions"
            }
            catch {
                Write-Warning "Connected, but failed to persist the refresh token for future sessions: $_"
            }
        }

        [PSCustomObject]@{
            Url           = $script:JIMConnection.Url
            AuthMethod    = 'OAuth'
            Connected     = $true
            ServerVersion = $serverVersion
            ExpiresAt     = $tokens.ExpiresAt
            Authorised    = $userInfo.authorised ?? $null
            Status        = $health.status ?? 'Connected'
        }
    }
    catch {
        $script:JIMConnection = $null
        throw "Authentication succeeded but failed to connect to JIM API: $_"
    }
}
