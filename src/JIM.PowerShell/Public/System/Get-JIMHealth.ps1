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
        Base URL of the JIM instance, e.g. "https://jim.example.com".
        If omitted, uses the URL from the current Connect-JIM session.

    .PARAMETER Ready
        Check the readiness probe instead of basic health. Verifies database
        connectivity and maintenance mode status.

    .PARAMETER Live
        Check the liveness probe instead of basic health. Confirms the process
        is running.

    .OUTPUTS
        PSCustomObject with status and timestamp properties.

    .EXAMPLE
        Get-JIMHealth -Url "https://jim.example.com"

    .EXAMPLE
        Get-JIMHealth -Ready

    .EXAMPLE
        Get-JIMHealth -Live
    #>
    [CmdletBinding(DefaultParameterSetName = 'Health')]
    [OutputType([PSCustomObject])]
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
