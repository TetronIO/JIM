# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    A minimal, in-memory SCIM 2.0 service provider for exercising the JIM SCIM 2.0 Client Connector
    against real HTTP.

.DESCRIPTION
    JIM is air-gap deployable and carries no third-party service dependency, so its own test provider is
    written here rather than pulled as a container image. It serves the discovery documents and a Users
    and Groups collection over HttpListener, which is enough to drive schema discovery, Full Import,
    Delta Import and export end to end.

    This is deliberately small. The exhaustive coverage of provider misbehaviour (expired cursors,
    advertised-but-rejected filters, clock skew, misreported totals) lives in the unit suite's
    MockScimProvider, which can be steered per test; this script exists to prove the connector works over
    a real socket, which no stubbed message handler can.

.PARAMETER Port
    The loopback port to listen on. JIM permits plain HTTP for loopback addresses only.

.PARAMETER UserCount
    How many users to serve. Enough to span several pages at a small page size.

.EXAMPLE
    pwsh ./test/integration/scim/Start-ScimTestProvider.ps1 -Port 5300
#>

[CmdletBinding()]
param(
    [int]$Port = 5300,
    [int]$UserCount = 25
)

$ErrorActionPreference = 'Stop'

$script:Users = @()
$script:Groups = @()
$script:NextId = 0

function New-ScimUser {
    param([string]$UserName, [string]$GivenName, [string]$FamilyName)

    $script:NextId++
    return [ordered]@{
        id         = "user-$($script:NextId)"
        userName   = $UserName
        active     = $true
        name       = [ordered]@{ givenName = $GivenName; familyName = $FamilyName }
        emails     = @([ordered]@{ value = "$UserName@example.com"; type = 'work' })
        meta       = [ordered]@{
            resourceType = 'User'
            lastModified = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
            version      = "W/`"$($script:NextId)`""
        }
    }
}

for ($i = 1; $i -le $UserCount; $i++) {
    $script:Users += New-ScimUser -UserName "user$i" -GivenName "User" -FamilyName "Number$i"
}

$script:NextId++
$script:Groups += [ordered]@{
    id          = 'group-1'
    displayName = 'Engineers'
    members     = @([ordered]@{ value = 'user-1' }, [ordered]@{ value = 'user-2' })
    meta        = [ordered]@{
        resourceType = 'Group'
        lastModified = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        version      = "W/`"$($script:NextId)`""
    }
}

$serviceProviderConfig = [ordered]@{
    schemas = @('urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig')
    patch   = @{ supported = $true }
    filter  = @{ supported = $true; maxResults = 200 }
    etag    = @{ supported = $true }
    bulk    = @{ supported = $false }
    sort    = @{ supported = $false }
    changePassword = @{ supported = $false }
    authenticationSchemes = @(@{ type = 'oauthbearertoken'; name = 'OAuth Bearer Token' })
}

$resourceTypes = [ordered]@{
    schemas      = @('urn:ietf:params:scim:api:messages:2.0:ListResponse')
    totalResults = 2
    Resources    = @(
        [ordered]@{ id = 'User'; name = 'User'; endpoint = '/Users'; schema = 'urn:ietf:params:scim:schemas:core:2.0:User' },
        [ordered]@{ id = 'Group'; name = 'Group'; endpoint = '/Groups'; schema = 'urn:ietf:params:scim:schemas:core:2.0:Group' }
    )
}

function Get-ListResponse {
    param([array]$Resources, [int]$StartIndex, [int]$Count, [string]$Filter)

    $matching = $Resources
    if ($Filter -and $Filter -match '^\s*meta\.lastModified\s+gt\s+"(?<value>[^"]+)"\s*$') {
        $watermark = [datetimeoffset]::Parse($Matches['value'])
        $matching = @($Resources | Where-Object { [datetimeoffset]::Parse($_.meta.lastModified) -gt $watermark })
    }

    $offset = [Math]::Max(0, $StartIndex - 1)
    $page = @($matching | Select-Object -Skip $offset -First $Count)

    return [ordered]@{
        schemas      = @('urn:ietf:params:scim:api:messages:2.0:ListResponse')
        totalResults = $matching.Count
        startIndex   = [Math]::Max(1, $StartIndex)
        itemsPerPage = $page.Count
        Resources    = $page
    }
}

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "[scim-test-provider] Listening on http://localhost:$Port/ with $UserCount user(s) and $($script:Groups.Count) group(s)."
Write-Host "[scim-test-provider] Press Ctrl+C to stop."

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response
        $path = $request.Url.AbsolutePath.TrimEnd('/')
        $body = $null
        $status = 200

        switch -Regex ($path) {
            '/ServiceProviderConfig$' { $body = $serviceProviderConfig }
            '/ResourceTypes$'         { $body = $resourceTypes }
            '/Schemas$'               { $status = 404 }   # exercises the connector's RFC 7643 core-schema fallback
            '/Users$' {
                $body = Get-ListResponse -Resources $script:Users `
                    -StartIndex ([int]($request.QueryString['startIndex'] ?? 1)) `
                    -Count ([int]($request.QueryString['count'] ?? 100)) `
                    -Filter $request.QueryString['filter']
            }
            '/Groups$' {
                $body = Get-ListResponse -Resources $script:Groups `
                    -StartIndex ([int]($request.QueryString['startIndex'] ?? 1)) `
                    -Count ([int]($request.QueryString['count'] ?? 100)) `
                    -Filter $request.QueryString['filter']
            }
            default { $status = 404 }
        }

        Write-Host "[scim-test-provider] $($request.HttpMethod) $($request.Url.PathAndQuery) -> $status"

        $response.StatusCode = $status
        $response.ContentType = 'application/scim+json'
        if ($null -ne $body) {
            $json = $body | ConvertTo-Json -Depth 10
            $buffer = [System.Text.Encoding]::UTF8.GetBytes($json)
            $response.ContentLength64 = $buffer.Length
            $response.OutputStream.Write($buffer, 0, $buffer.Length)
        }
        $response.OutputStream.Close()
    }
}
finally {
    $listener.Stop()
    $listener.Close()
}
