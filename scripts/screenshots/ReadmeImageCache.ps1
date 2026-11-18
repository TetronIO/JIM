# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Purges GitHub's cached copies of the README's screenshots once junctional.io serves a release's new ones (#582).

.DESCRIPTION
    GitHub shows a README's images through its image proxy (camo.githubusercontent.com), which keeps its own copy. The
    README reads its homepage screenshots from fixed junctional.io addresses that every release overwrites, and the
    proxy went on showing v0.17.0's after v0.18.0 published new ones. A PURGE request to the proxy address drops its
    copy, so the next view fetches the image again.

    Purging before the site has deployed would only cache the old image again, so each image is purged once the site
    serves exactly the bytes its repository holds, and checked afterwards: the proxy must then serve them too.
#>

Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TourPullRequest.ps1')

# The proxied images in rendered README HTML whose original address starts with $OriginPrefix. A proxy address ends
# with the original address in hex: https://camo.githubusercontent.com/<signature>/<hex of the address>.
function Get-ReadmeProxiedImage {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Html,
        [string]$OriginPrefix = 'https://junctional.io/assets/screenshots/'
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($match in [regex]::Matches($Html, 'https://camo\.githubusercontent\.com/[0-9a-f]+/(?<hex>[0-9a-f]+)')) {
        $proxy = $match.Value
        if (-not $seen.Add($proxy)) { continue }
        $hex = $match.Groups['hex'].Value
        $bytes = [byte[]]::new($hex.Length / 2)
        for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16) }
        $origin = [System.Text.Encoding]::UTF8.GetString($bytes)
        if ($origin.StartsWith($OriginPrefix, [StringComparison]::Ordinal)) {
            [pscustomobject]@{ ProxyUrl = $proxy; Origin = $origin }
        }
    }
}

# The README as GitHub renders it, which is where the proxy addresses (signed by GitHub) come from.
function Get-RenderedReadme {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [string]$Token
    )

    $headers = @{ Accept = 'application/vnd.github.html'; 'X-GitHub-Api-Version' = '2022-11-28' }
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $content = (Invoke-WebRequest -Uri "https://api.github.com/repos/$Repository/readme" -Headers $headers).Content
    # GitHub labels the rendered README with a media type Invoke-WebRequest does not treat as text, so it arrives as bytes.
    if ($content -is [byte[]]) { $content = [System.Text.Encoding]::UTF8.GetString($content) }
    $content
}

# The bytes an address serves. Its own function so tests can stand in for the network.
function Get-UrlBytes {
    param([Parameter(Mandatory)][string]$Uri)
    , ([byte[]](Invoke-WebRequest -Uri $Uri -Headers @{ 'Cache-Control' = 'no-cache' }).Content)
}

# Asks the proxy to drop its copy of an image.
function Send-ProxyPurge {
    param([Parameter(Mandatory)][string]$Uri)
    # PURGE is not one of -Method's names.
    Invoke-WebRequest -Uri $Uri -CustomMethod PURGE | Out-Null
}

function Get-BytesHash([byte[]]$Bytes) {
    [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes))
}

<#
.SYNOPSIS
    Purges the proxy's copy of each README screenshot once the site serves what its repository holds.
.OUTPUTS
    One object per image: Origin; ProxyUrl; Status, which is Cleared, SiteStale (the site never served the new image,
    so it was not purged) or ProxyStale (purged, but the proxy still serves something else); and Message.
#>
function Clear-ReadmeImageCache {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Html,
        [Parameter(Mandatory)][string]$SiteRepository,
        [Parameter(Mandatory)][string]$Token,
        [string]$SiteBranch = 'main',
        [int]$TimeoutSeconds = 1800,
        [int]$PollSeconds = 30
    )

    $images = @(Get-ReadmeProxiedImage -Html $Html)
    if ($images.Count -eq 0) {
        throw 'The README shows no screenshots through GitHub''s image proxy; has its homepage image moved?'
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    foreach ($image in $images) {
        # A fixed address such as /assets/screenshots/homepage-dark.png is served from public/ in the site's repository.
        $path = 'public' + ([uri]$image.Origin).AbsolutePath
        $file = Invoke-GitHubApi -Method GET -Path "repos/$SiteRepository/contents/${path}?ref=$SiteBranch" -Token $Token
        $expected = Get-BytesHash ([Convert]::FromBase64String(($file.content -replace '\s', '')))

        $result = [pscustomobject]@{ Origin = $image.Origin; ProxyUrl = $image.ProxyUrl; Status = 'Cleared'; Message = $null }
        while ((Get-BytesHash (Get-UrlBytes -Uri $image.Origin)) -ne $expected) {
            if ((Get-Date) -ge $deadline) {
                $result.Status = 'SiteStale'
                $result.Message = "$($image.Origin) still did not serve $path from $SiteRepository after $TimeoutSeconds seconds, so the README's copy was not purged."
                break
            }
            Start-Sleep -Seconds $PollSeconds
        }
        if ($result.Status -eq 'Cleared') {
            Send-ProxyPurge -Uri $image.ProxyUrl
            if ((Get-BytesHash (Get-UrlBytes -Uri $image.ProxyUrl)) -ne $expected) {
                $result.Status = 'ProxyStale'
                $result.Message = "GitHub's copy of $($image.Origin) was purged but still differs from the site's."
            }
        }
        $result
    }
}
