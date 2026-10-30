# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refuses a release whose images cannot be pulled without signing in: exits 0 only when every image named can be
    pulled anonymously from ghcr.io at the version given.

.DESCRIPTION
    The installer and the installation guide pull JIM's images from ghcr.io without credentials, so an image that is
    private fails every online install and upgrade, however good the release. A new package on ghcr.io starts private,
    and nothing else in the release would notice: the release workflow pushes and smoke-tests the images signed in.
    The deployment lab's online cells found JIM's three packages private after v0.17.0 (#1876).

    For each image this asks ghcr.io for an anonymous pull token, as Docker and Podman do, and then for the image's
    manifest at the version, without any credential. Either refusal fails the image, with a message naming it and the
    remedy: make the package public (organisation Settings, Packages, then the package's settings, Change visibility).

    release.yml runs it after the images are pushed and before the GitHub Release is created, so a release with a
    private image is never published.

.PARAMETER Image
    The images, as ghcr.io/<owner>/<name> (any case).

.PARAMETER Version
    The tag to check, for example 0.17.0.

.EXAMPLE
    pwsh -File ./scripts/Test-PublicImage.ps1 -Image ghcr.io/tetronio/jim-web, ghcr.io/tetronio/jim-worker -Version 0.17.0
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Image,

    [Parameter(Mandatory)]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$accept = 'application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json'
$failed = @()
# -File passes a comma-separated list as one string.
foreach ($name in @($Image -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    $reference = $name.ToLowerInvariant()
    if ($reference -notmatch '^ghcr\.io/(?<repository>[a-z0-9._-]+/[a-z0-9._/-]+)$') {
        throw "Not a ghcr.io image: $name"
    }
    $repository = $Matches.repository
    $problem = $null
    try {
        $token = (Invoke-RestMethod -Uri "https://ghcr.io/token?scope=repository:${repository}:pull").token
        if (-not $token) {
            $problem = 'ghcr.io gives no anonymous pull token for it'
        }
        else {
            $null = Invoke-WebRequest -Uri "https://ghcr.io/v2/$repository/manifests/$Version" -Method Head `
                -Headers @{ Authorization = "Bearer $token"; Accept = $accept }
        }
    }
    catch {
        $status = try { [int]$_.Exception.Response.StatusCode } catch { 0 }
        $problem = if ($status -eq 404) { "ghcr.io has no $Version of it" }
        elseif ($status) { "ghcr.io answers $status to an anonymous pull, so it is private" }
        else { "$($_.Exception.Message)" }
    }
    if ($problem) {
        Write-Host "::error title=Image cannot be pulled anonymously: ${reference}:$Version::$problem. Customers pull it without signing in, so every online install and upgrade would fail. If it is private, make the package public: organisation Settings, Packages, the package's settings, Change visibility."
        $failed += $reference
    }
    else {
        Write-Host "${reference}:$Version can be pulled anonymously"
    }
}
exit $(if ($failed) { 1 } else { 0 })
