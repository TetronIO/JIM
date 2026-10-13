# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Test-JIMConnection {
    <#
    .SYNOPSIS
        Tests the connection to a JIM instance.

    .DESCRIPTION
        Verifies that the current JIM connection is valid and the instance is reachable.
        Returns connection status information including the JIM health status and
        authentication method used.

    .PARAMETER Quiet
        Returns only $true or $false instead of the connection's status.

    .OUTPUTS
        The connection's status; with -Quiet, $true if connected and otherwise $false.

        JIM.ConnectionStatus: By default
        Connected (bool): Whether a connection exists and JIM answered.
        Url (string): The JIM instance's address.
        AuthMethod (string): How the connection authenticated: ApiKey or OAuth.
        ServerVersion (string): The JIM version the instance reported.
        Status (string): JIM's health status.
        Message (string): A sentence describing the result.
        TokenExpiresAt (datetime): When the access token expires; OAuth only.

        System.Boolean: With -Quiet

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
    [OutputType('JIM.ConnectionStatus')]
    [OutputType([bool])]
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
