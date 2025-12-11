function Connect-JIM {
    <#
    .SYNOPSIS
        Connects to a JIM instance for administration.

    .DESCRIPTION
        Establishes a connection to a JIM (Junctional Identity Manager) instance.
        This connection is required before using any other JIM cmdlets.

        Currently supports API key authentication for non-interactive scenarios
        such as CI/CD pipelines, integration testing, and automation scripts.

    .PARAMETER Url
        The base URL of the JIM instance, e.g., 'https://jim.company.com' or 'http://localhost:5200'.

    .PARAMETER ApiKey
        The API key for authentication. API keys can be created in the JIM web interface
        under Admin > API Keys.

    .OUTPUTS
        Returns the connection information on success.

    .EXAMPLE
        Connect-JIM -Url "https://jim.company.com" -ApiKey "jim_abc123..."

        Connects to a JIM instance using an API key.

    .EXAMPLE
        Connect-JIM -Url "http://localhost:5200" -ApiKey $env:JIM_API_KEY

        Connects to a local JIM instance using an API key from an environment variable.

    .NOTES
        API keys can be created in the JIM web interface under Admin > API Keys.
        Store API keys securely - they provide full access to JIM administration.

    .LINK
        Disconnect-JIM
        Test-JIMConnection
        https://github.com/TetronIO/JIM
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Url,

        [Parameter(Mandatory, Position = 1)]
        [ValidateNotNullOrEmpty()]
        [string]$ApiKey
    )

    Write-Verbose "Connecting to JIM at $Url"

    # Validate URL format
    if (-not ($Url -match '^https?://')) {
        throw "Invalid URL format. URL must start with http:// or https://"
    }

    # Store connection info
    $script:JIMConnection = [PSCustomObject]@{
        Url       = $Url.TrimEnd('/')
        ApiKey    = $ApiKey
        Connected = $false
    }

    # Test the connection
    try {
        Write-Verbose "Testing connection to JIM..."
        $health = Invoke-JIMApi -Endpoint '/api/v1/health'

        $script:JIMConnection.Connected = $true

        Write-Verbose "Successfully connected to JIM"

        # Return connection info (without exposing full API key)
        $keyPreview = if ($ApiKey.Length -gt 12) {
            $ApiKey.Substring(0, 8) + "..." + $ApiKey.Substring($ApiKey.Length - 4)
        }
        else {
            "***"
        }

        [PSCustomObject]@{
            Url       = $script:JIMConnection.Url
            ApiKey    = $keyPreview
            Connected = $true
            Status    = $health.status ?? 'Connected'
        }
    }
    catch {
        $script:JIMConnection = $null
        throw "Failed to connect to JIM at $Url`: $_"
    }
}
