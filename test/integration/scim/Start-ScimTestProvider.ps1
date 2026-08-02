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

    Bulk requests are answered by replaying each operation through the same handlers a standalone
    request reaches, so entity tags, missing resources and rejections behave identically whether a change
    arrives on its own or inside a batch. If they did not, a bulk export could pass here while failing
    against every real provider.

.PARAMETER Port
    The loopback port to listen on. JIM permits plain HTTP for loopback addresses only.

.PARAMETER UserCount
    How many users to serve. Enough to span several pages at a small page size.

.PARAMETER DisableBulk
    Advertise no bulk support and answer /Bulk with 404, so the connector's per-object path is exercised
    against the same data.

.PARAMETER BulkMaxOperations
    The cap advertised on operations per bulk request, and enforced. Small values prove the connector
    splits batches rather than trusting the provider to cope.

.EXAMPLE
    pwsh ./test/integration/scim/Start-ScimTestProvider.ps1 -Port 5300

.EXAMPLE
    pwsh ./test/integration/scim/Start-ScimTestProvider.ps1 -Port 5300 -BulkMaxOperations 5
#>

[CmdletBinding()]
param(
    [int]$Port = 5300,
    [int]$UserCount = 25,
    [switch]$DisableBulk,
    [int]$BulkMaxOperations = 0
)

$ErrorActionPreference = 'Stop'

$script:Users = [System.Collections.ArrayList]::new()
$script:Groups = [System.Collections.ArrayList]::new()
$script:NextId = 0
$script:SupportsBulk = -not $DisableBulk

function Get-Instant {
    return (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}

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
            lastModified = Get-Instant
            version      = "W/`"$($script:NextId)`""
        }
    }
}

for ($i = 1; $i -le $UserCount; $i++) {
    [void]$script:Users.Add((New-ScimUser -UserName "user$i" -GivenName "User" -FamilyName "Number$i"))
}

$script:NextId++
[void]$script:Groups.Add([ordered]@{
    id          = 'group-1'
    displayName = 'Engineers'
    members     = @([ordered]@{ value = 'user-1' }, [ordered]@{ value = 'user-2' })
    meta        = [ordered]@{
        resourceType = 'Group'
        lastModified = Get-Instant
        version      = "W/`"$($script:NextId)`""
    }
})

function Get-ServiceProviderConfig {
    $bulk = [ordered]@{ supported = $script:SupportsBulk }
    if ($script:SupportsBulk -and $BulkMaxOperations -gt 0) {
        $bulk['maxOperations'] = $BulkMaxOperations
    }

    return [ordered]@{
        schemas = @('urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig')
        patch   = @{ supported = $true }
        filter  = @{ supported = $true; maxResults = 200 }
        etag    = @{ supported = $true }
        bulk    = $bulk
        sort    = @{ supported = $false }
        changePassword = @{ supported = $false }
        authenticationSchemes = @(@{ type = 'oauthbearertoken'; name = 'OAuth Bearer Token' })
    }
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

function New-ScimError {
    param([int]$Status, [string]$Detail, [string]$ScimType)

    $error = [ordered]@{
        schemas = @('urn:ietf:params:scim:api:messages:2.0:Error')
        status  = "$Status"
    }
    if ($ScimType) { $error['scimType'] = $ScimType }
    if ($Detail) { $error['detail'] = $Detail }
    return $error
}

function Get-Collection {
    param([string]$Name)

    switch ($Name) {
        'Users'  { return $script:Users }
        'Groups' { return $script:Groups }
        default  { return $null }
    }
}

# ─── writes ───

function Add-Resource {
    param([System.Collections.ArrayList]$Collection, [string]$ResourceType, [hashtable]$Body)

    $script:NextId++
    $resource = [ordered]@{ id = "$($ResourceType.ToLowerInvariant())-created-$($script:NextId)" }

    foreach ($member in $Body.Keys) {
        if ($member -in @('schemas', 'id', 'meta')) { continue }
        $resource[$member] = $Body[$member]
    }

    $resource['meta'] = [ordered]@{
        resourceType = $ResourceType
        lastModified = Get-Instant
        version      = "W/`"$($script:NextId)`""
    }

    [void]$Collection.Add($resource)
    return $resource
}

<#
    PATCH is acknowledged rather than applied faithfully: what is under test is the request the connector
    composed and the outcome it recorded, and implementing SCIM path semantics well enough to assert
    against would make this script the thing being tested. Only the entity tag moves on, which is what a
    following export or import actually depends on.
#>
function Update-ResourceVersion {
    param([object]$Resource)

    $script:NextId++
    $Resource['meta']['lastModified'] = Get-Instant
    $Resource['meta']['version'] = "W/`"$($script:NextId)`""
}

function Set-Resource {
    param([object]$Resource, [hashtable]$Body)

    foreach ($member in @($Resource.Keys)) {
        if ($member -in @('id', 'meta')) { continue }
        $Resource.Remove($member)
    }
    foreach ($member in $Body.Keys) {
        if ($member -in @('schemas', 'id', 'meta')) { continue }
        $Resource[$member] = $Body[$member]
    }

    Update-ResourceVersion -Resource $Resource
}

# ─── request handling ───

<#
.SYNOPSIS
    Answers one SCIM request, whether it arrived on its own or as an operation inside a bulk request.

.OUTPUTS
    A hashtable with Status and, where there is one, Body.
#>
function Invoke-ScimRequest {
    param(
        [string]$Method,
        [string]$Path,
        [System.Collections.Specialized.NameValueCollection]$Query,
        [string]$Body,
        [string]$IfMatch
    )

    $path = $Path.TrimEnd('/')

    if ($path -match '/ServiceProviderConfig$') { return @{ Status = 200; Body = Get-ServiceProviderConfig } }
    if ($path -match '/ResourceTypes$')         { return @{ Status = 200; Body = $resourceTypes } }

    # Serving no /Schemas exercises the connector's RFC 7643 core-schema fallback, which is the position
    # plenty of real providers leave a client in.
    if ($path -match '/Schemas$')               { return @{ Status = 404 } }

    if ($path -match '/Bulk$') {
        if ($Method -ne 'POST') { return @{ Status = 405; Body = New-ScimError -Status 405 -Detail 'The bulk endpoint accepts POST only.' } }
        return Invoke-BulkRequest -Body $Body
    }

    $segments = @($path.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
    if ($segments.Count -eq 0) { return @{ Status = 404 } }

    $collectionName = $segments[-1]
    $collection = Get-Collection -Name $collectionName

    if ($null -ne $collection) {
        if ($Method -eq 'POST') {
            $parsed = ConvertFrom-JsonBody -Json $Body
            if ($null -eq $parsed) { return @{ Status = 400; Body = New-ScimError -Status 400 -ScimType 'invalidSyntax' -Detail 'The request body was not valid JSON.' } }
            $resourceType = $collectionName.Substring(0, $collectionName.Length - 1)
            return @{ Status = 201; Body = Add-Resource -Collection $collection -ResourceType $resourceType -Body $parsed }
        }

        return @{
            Status = 200
            Body   = Get-ListResponse -Resources $collection.ToArray() `
                        -StartIndex ([int]($Query['startIndex'] ?? 1)) `
                        -Count ([int]($Query['count'] ?? 100)) `
                        -Filter $Query['filter']
        }
    }

    # A request against one resource: /Users/{id}.
    if ($segments.Count -lt 2) { return @{ Status = 404 } }
    $collection = Get-Collection -Name $segments[-2]
    if ($null -eq $collection) { return @{ Status = 404 } }

    $id = [uri]::UnescapeDataString($segments[-1])
    $resource = $collection | Where-Object { $_.id -eq $id } | Select-Object -First 1
    if ($null -eq $resource) { return @{ Status = 404; Body = New-ScimError -Status 404 -Detail 'No such resource.' } }

    # RFC 7644 section 3.14: a write carrying a stale entity tag is refused rather than allowed to
    # overwrite whatever changed the resource in between.
    if ($Method -ne 'GET' -and $IfMatch -and $IfMatch -ne $resource.meta.version) {
        return @{ Status = 412; Body = New-ScimError -Status 412 -Detail 'The resource has changed since it was read.' }
    }

    switch ($Method) {
        'GET' { return @{ Status = 200; Body = $resource } }
        'DELETE' {
            $collection.Remove($resource)
            return @{ Status = 204 }
        }
        'PUT' {
            $parsed = ConvertFrom-JsonBody -Json $Body
            if ($null -eq $parsed) { return @{ Status = 400; Body = New-ScimError -Status 400 -ScimType 'invalidSyntax' -Detail 'The request body was not valid JSON.' } }
            Set-Resource -Resource $resource -Body $parsed
            return @{ Status = 200; Body = $resource }
        }
        'PATCH' {
            Update-ResourceVersion -Resource $resource
            return @{ Status = 200; Body = $resource }
        }
        default { return @{ Status = 405 } }
    }
}

function ConvertFrom-JsonBody {
    param([string]$Json)

    if ([string]::IsNullOrWhiteSpace($Json)) { return $null }
    try { return $Json | ConvertFrom-Json -AsHashtable } catch { return $null }
}

<#
.SYNOPSIS
    Applies a bulk request (RFC 7644 section 3.7) by replaying each operation through Invoke-ScimRequest.
#>
function Invoke-BulkRequest {
    param([string]$Body)

    if (-not $script:SupportsBulk) {
        return @{ Status = 404; Body = New-ScimError -Status 404 -Detail 'This service provider has no bulk endpoint.' }
    }

    $parsed = ConvertFrom-JsonBody -Json $Body
    if ($null -eq $parsed -or -not $parsed.ContainsKey('Operations')) {
        return @{ Status = 400; Body = New-ScimError -Status 400 -ScimType 'invalidSyntax' -Detail 'The bulk request carried no operations.' }
    }

    $operations = @($parsed['Operations'])
    if ($BulkMaxOperations -gt 0 -and $operations.Count -gt $BulkMaxOperations) {
        return @{ Status = 400; Body = New-ScimError -Status 400 -ScimType 'tooMany' -Detail "This provider accepts at most $BulkMaxOperations operations per bulk request." }
    }

    $results = foreach ($operation in $operations) {
        $method = if ($operation.ContainsKey('method')) { $operation['method'] } else { 'POST' }
        $operationPath = if ($operation.ContainsKey('path')) { $operation['path'] } else { '' }
        $data = if ($operation.ContainsKey('data') -and $null -ne $operation['data']) { $operation['data'] | ConvertTo-Json -Depth 10 -Compress } else { $null }
        $version = if ($operation.ContainsKey('version')) { $operation['version'] } else { $null }

        $inner = Invoke-ScimRequest -Method $method.ToUpperInvariant() -Path $operationPath `
                    -Query ([System.Web.HttpUtility]::ParseQueryString('')) -Body $data -IfMatch $version

        $result = [ordered]@{
            method = $method
            status = "$($inner.Status)"
        }
        if ($operation.ContainsKey('bulkId')) { $result['bulkId'] = $operation['bulkId'] }

        if ($inner.Status -ge 200 -and $inner.Status -lt 300) {
            $assignedId = if ($inner.Body -and $inner.Body.id) { $inner.Body.id } else { $null }
            $result['location'] = if ($assignedId -and $method.ToUpperInvariant() -eq 'POST') {
                "http://localhost:$Port$($operationPath.TrimEnd('/'))/$assignedId"
            } else {
                "http://localhost:$Port$operationPath"
            }
        }
        elseif ($inner.Body) {
            $result['response'] = $inner.Body
        }

        $result
    }

    return @{
        Status = 200
        Body   = [ordered]@{
            schemas    = @('urn:ietf:params:scim:api:messages:2.0:BulkResponse')
            Operations = @($results)
        }
    }
}

Add-Type -AssemblyName System.Web

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "[scim-test-provider] Listening on http://localhost:$Port/ with $UserCount user(s) and $($script:Groups.Count) group(s)."
Write-Host "[scim-test-provider] Bulk operations: $(if ($script:SupportsBulk) { if ($BulkMaxOperations -gt 0) { "supported, max $BulkMaxOperations per request" } else { 'supported, no stated limit' } } else { 'not supported' })."
Write-Host "[scim-test-provider] Press Ctrl+C to stop."

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response

        $requestBody = $null
        if ($request.HasEntityBody) {
            $reader = [System.IO.StreamReader]::new($request.InputStream, $request.ContentEncoding)
            $requestBody = $reader.ReadToEnd()
            $reader.Dispose()
        }

        $result = Invoke-ScimRequest -Method $request.HttpMethod -Path $request.Url.AbsolutePath `
                    -Query $request.QueryString -Body $requestBody -IfMatch $request.Headers['If-Match']

        Write-Host "[scim-test-provider] $($request.HttpMethod) $($request.Url.PathAndQuery) -> $($result.Status)"

        $response.StatusCode = $result.Status
        $response.ContentType = 'application/scim+json'

        if ($null -ne $result.Body) {
            if ($result.Body.meta -and $result.Body.meta.version) {
                $response.Headers.Add('ETag', $result.Body.meta.version)
            }
            $json = $result.Body | ConvertTo-Json -Depth 10
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
