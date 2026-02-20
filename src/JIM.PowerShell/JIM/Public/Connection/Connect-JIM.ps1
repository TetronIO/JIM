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

    .PARAMETER Url
        The base URL of the JIM instance, e.g., 'https://jim.company.com' or 'http://localhost:5200'.

    .PARAMETER ApiKey
        The API key for authentication. API keys can be created in the JIM web interface
        under Admin > API Keys. When specified, skips interactive authentication.

    .PARAMETER Force
        Forces re-authentication even if a valid session exists.

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
    return Connect-JIMInteractive -BaseUrl $baseUrl -Force:$Force -TimeoutSeconds $TimeoutSeconds
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

        Write-Verbose "Successfully connected to JIM using API key"

        # Return connection info (without exposing full API key)
        $keyPreview = if ($ApiKey.Length -gt 12) {
            $ApiKey.Substring(0, 8) + "..." + $ApiKey.Substring($ApiKey.Length - 4)
        }
        else {
            "***"
        }

        [PSCustomObject]@{
            Url        = $script:JIMConnection.Url
            AuthMethod = 'ApiKey'
            ApiKey     = $keyPreview
            Connected  = $true
            Status     = $health.status ?? 'Connected'
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

                    Write-Verbose "Successfully refreshed access token"
                    return [PSCustomObject]@{
                        Url        = $script:JIMConnection.Url
                        AuthMethod = 'OAuth'
                        Connected  = $true
                        ExpiresAt  = $tokens.ExpiresAt
                        Status     = 'Connected (refreshed)'
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

        Write-Host ""
        Write-Host "Successfully connected to JIM!" -ForegroundColor Green
        Write-Host ""

        [PSCustomObject]@{
            Url        = $script:JIMConnection.Url
            AuthMethod = 'OAuth'
            Connected  = $true
            ExpiresAt  = $tokens.ExpiresAt
            Status     = $health.status ?? 'Connected'
        }
    }
    catch {
        $script:JIMConnection = $null
        throw "Authentication succeeded but failed to connect to JIM API: $_"
    }
}
