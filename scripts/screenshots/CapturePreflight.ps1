# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Checks, before a browser starts, that Capture-Screenshots.ps1 will be able to sign in (#582).

.DESCRIPTION
    The capture signs in through whichever identity provider the portal redirects browsers to. When that address is
    not answering, every image fails with the same "connection refused" and nothing says which address or why. On the
    development stack the address is a bridge to Keycloak (localhost:8181 forwarding to 8180), which
    Run-IntegrationTests.ps1 starts only when socat is installed, so a host without socat loads its data successfully
    and then cannot capture a single image.

    The requests are made without a proxy, as the browser makes them.
#>

# Where the portal's front page redirects a browser that has not signed in: the Location header as sent (absolute or
# relative), or $null when the page does not redirect.
function Get-PortalSignInRedirect {
    param([Parameter(Mandatory)][string]$BaseUrl)

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(10)
        $response = $client.GetAsync($BaseUrl.TrimEnd('/') + '/').GetAwaiter().GetResult()
        try {
            $status = [int]$response.StatusCode
            if ($status -lt 300 -or $status -ge 400 -or $null -eq $response.Headers.Location) { return $null }
            return $response.Headers.Location.OriginalString
        }
        finally { $response.Dispose() }
    }
    finally { $client.Dispose() }
}

# Whether anything answers HTTP at an origin; any status at all counts as answering.
function Test-HttpOriginAnswers {
    param([Parameter(Mandatory)][string]$Origin)

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(10)
        $client.GetAsync($Origin.TrimEnd('/') + '/').GetAwaiter().GetResult().Dispose()
        return $true
    }
    catch [System.Net.Http.HttpRequestException] { return $false }
    catch [System.Threading.Tasks.TaskCanceledException] { return $false }
    finally { $client.Dispose() }
}

# Why signing in to the portal at BaseUrl would fail, or $null when nothing is in the way.
function Get-ScreenshotSignInProblem {
    param([Parameter(Mandatory)][string]$BaseUrl)

    $baseUri = [Uri]$BaseUrl
    $baseOrigin = $baseUri.GetLeftPart([UriPartial]::Authority)

    try { $location = Get-PortalSignInRedirect -BaseUrl $BaseUrl }
    catch [System.Net.Http.HttpRequestException] { return "Nothing answers at $baseOrigin; start the stack, or run with -PrepareData to build it and load the data." }
    catch [System.Threading.Tasks.TaskCanceledException] { return "$baseOrigin did not answer within 10 seconds; check that the portal has finished starting." }

    if ([string]::IsNullOrEmpty($location)) { return $null }
    $signInOrigin = [Uri]::new($baseUri, $location).GetLeftPart([UriPartial]::Authority)
    if ($signInOrigin -eq $baseOrigin) { return $null }

    if (Test-HttpOriginAnswers -Origin $signInOrigin) { return $null }
    "The portal sends browsers to $signInOrigin to sign in, and nothing answers there, so every capture would fail at " +
    "sign-in. On the development stack that address is a bridge to Keycloak (port 8180), which " +
    "Run-IntegrationTests.ps1 starts only when socat is installed; install socat and start the bridge, then run again."
}
