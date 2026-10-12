# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMHealth {
    <#
    .SYNOPSIS
        Retrieves the health, readiness, or liveness status of a JIM instance.

    .DESCRIPTION
        Calls the JIM health endpoints to check the status of a JIM instance.
        By default, returns the basic health status. Use -Ready to check database
        and service readiness, or -Live to check process liveness.

        These endpoints do not require authentication. Use -Url for standalone
        checks, or omit it to use the URL from an active Connect-JIM session.

    .PARAMETER Url
        The JIM instance's base URL, for example https://jim.example.com; the connected instance's when omitted.

    .PARAMETER Ready
        Checks readiness instead: that the database is reachable and JIM is not in maintenance mode.

    .PARAMETER Live
        Checks liveness instead: that the process is running.

    .OUTPUTS
        One object with the probe's Status and Timestamp. A readiness check that fails is reported as an error
        rather than returned.

    .EXAMPLE
        Get-JIMHealth -Url "https://jim.example.com"

    .EXAMPLE
        Get-JIMHealth -Ready

    .EXAMPLE
        Get-JIMHealth -Live
    #>
    [CmdletBinding(DefaultParameterSetName = 'Health')]
    [OutputType('JIM.HealthStatusResponse')]
    param(
        [Parameter(Position = 0)]
        [string]$Url,

        [Parameter(Mandatory, ParameterSetName = 'Ready')]
        [switch]$Ready,

        [Parameter(Mandatory, ParameterSetName = 'Live')]
        [switch]$Live
    )

    process {
        $baseUrl = Resolve-JIMBaseUrl -Url $Url
        if (-not $baseUrl) { return }

        $endpoint = switch ($PSCmdlet.ParameterSetName) {
            'Ready' { '/api/v1/health/ready' }
            'Live'  { '/api/v1/health/live' }
            default { '/api/v1/health' }
        }

        $uri = "$($baseUrl.TrimEnd('/'))$endpoint"

        Write-Verbose "Checking health: $uri"

        try {
            $response = Invoke-RestMethod -Uri $uri -Method 'GET' -Headers @{ 'Accept' = 'application/json' } -ErrorAction Stop -MaximumRedirection 0
            $response
        }
        catch {
            Write-Error "Failed to check health at $uri`: $_"
        }
    }
}
