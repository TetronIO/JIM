function Test-JIMConnection {
    <#
    .SYNOPSIS
        Tests the connection to a JIM instance.

    .DESCRIPTION
        Verifies that the current JIM connection is valid and the instance is reachable.
        Returns connection status information including the JIM health status.

    .PARAMETER Quiet
        If specified, returns only $true or $false instead of the full status object.

    .OUTPUTS
        By default, returns a PSCustomObject with connection details.
        With -Quiet, returns $true if connected, $false otherwise.

    .EXAMPLE
        Test-JIMConnection

        Returns detailed connection status.

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
            Connected = $false
            Url       = $null
            Status    = 'Not connected'
            Message   = 'Use Connect-JIM to establish a connection.'
        }
    }

    try {
        Write-Verbose "Testing connection to $($script:JIMConnection.Url)"
        $health = Invoke-JIMApi -Endpoint '/api/v1/health'

        if ($Quiet) {
            return $true
        }

        [PSCustomObject]@{
            Connected = $true
            Url       = $script:JIMConnection.Url
            Status    = $health.status ?? 'Healthy'
            Message   = 'Connection successful'
        }
    }
    catch {
        if ($Quiet) {
            return $false
        }

        [PSCustomObject]@{
            Connected = $false
            Url       = $script:JIMConnection.Url
            Status    = 'Error'
            Message   = $_.Exception.Message
        }
    }
}
