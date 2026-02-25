function Test-JIMConnection {
    <#
    .SYNOPSIS
        Tests the connection to a JIM instance.

    .DESCRIPTION
        Verifies that the current JIM connection is valid and the instance is reachable.
        Returns connection status information including the JIM health status and
        authentication method used.

    .PARAMETER Quiet
        If specified, returns only $true or $false instead of the full status object.

    .OUTPUTS
        By default, returns a PSCustomObject with connection details including:
        - Connected: Whether a connection exists
        - Url: The JIM instance URL
        - AuthMethod: The authentication method (ApiKey or OAuth)
        - Status: The JIM health status
        - Message: A status message
        - TokenExpiresAt: (OAuth only) When the access token expires

        With -Quiet, returns $true if connected, $false otherwise.

    .EXAMPLE
        Test-JIMConnection

        Returns detailed connection status including authentication method.

    .EXAMPLE
        Test-JIMConnection -Quiet

        Returns $true if connected, $false otherwise.

    .EXAMPLE
        if (Test-JIMConnection -Quiet) { Get-JIMConnectedSystem }

        Conditionally runs a command if connected.

    .LINK
        Connect-JIM
        Disconnect-JIM
    #>
    [CmdletBinding()]
    param(
        [switch]$Quiet
    )

    if (-not $script:JIMConnection) {
        if ($Quiet) {
            return $false
        }
        return [PSCustomObject]@{
            Connected      = $false
            Url            = $null
            AuthMethod     = $null
            ServerVersion  = $null
            Status         = 'Not connected'
            Message        = 'Use Connect-JIM to establish a connection.'
            TokenExpiresAt = $null
        }
    }

    try {
        Write-Verbose "Testing connection to $($script:JIMConnection.Url)"
        $health = Invoke-JIMApi -Endpoint '/api/v1/health'

        if ($Quiet) {
            return $true
        }

        $result = [PSCustomObject]@{
            Connected      = $true
            Url            = $script:JIMConnection.Url
            AuthMethod     = $script:JIMConnection.AuthMethod ?? 'Unknown'
            ServerVersion  = $script:JIMConnection.ServerVersion
            Status         = $health.status ?? 'Healthy'
            Message        = 'Connection successful'
            TokenExpiresAt = $null
        }

        # Add token expiry info for OAuth connections
        if ($script:JIMConnection.AuthMethod -eq 'OAuth' -and $script:JIMConnection.TokenExpiresAt) {
            $result.TokenExpiresAt = $script:JIMConnection.TokenExpiresAt

            $timeRemaining = $script:JIMConnection.TokenExpiresAt - (Get-Date)
            if ($timeRemaining.TotalMinutes -lt 5) {
                $result.Message = "Connection successful (token expires soon)"
            }
            elseif ($timeRemaining.TotalMinutes -lt 0) {
                $result.Message = "Connection successful (token expired, will refresh on next request)"
            }
        }

        return $result
    }
    catch {
        if ($Quiet) {
            return $false
        }

        [PSCustomObject]@{
            Connected      = $false
            Url            = $script:JIMConnection.Url
            AuthMethod     = $script:JIMConnection.AuthMethod ?? 'Unknown'
            ServerVersion  = $script:JIMConnection.ServerVersion
            Status         = 'Error'
            Message        = $_.Exception.Message
            TokenExpiresAt = $null
        }
    }
}
