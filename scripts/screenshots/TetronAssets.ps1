# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refreshes the JIM screenshots on tetron.io from a captured screenshot library (#582).

.DESCRIPTION
    tetron.io's homepage shows JIM's home page in its hero: assets/images/homepage-dark.png, which the site's
    resized-figure shortcode resizes to WebP and links at full size. A refresh overwrites that file with the release's
    capture, the same image junctional.io's homepage shows.

    Before writing anything it checks the site still uses each image: a page must name its path (relative to assets/,
    as the shortcode does) and the file must already exist. A redesign that drops or moves the screenshot stops the
    publish with a reason, rather than having every release commit a file nothing on the site shows.
#>

Set-StrictMode -Version Latest

# Each image the site shows, by its path in the site, and the capture it comes from.
$script:TetronSiteImages = [ordered]@{
    'assets/images/homepage-dark.png' = 'homepage-dark.png'
}

# Where the site's pages and templates live; an image no file here names is not on the site.
$script:TetronSiteSourceFolders = @('content', 'layouts')

# True when a page or template names the image, by its path relative to assets/ (how Hugo's resources.Get takes it).
function Test-TetronSiteImageReferenced {
    param(
        [Parameter(Mandatory)][string]$SitePath,
        [Parameter(Mandatory)][string]$ImagePath
    )

    $resourcePath = $ImagePath -replace '^assets/', ''
    $folders = $script:TetronSiteSourceFolders | ForEach-Object { Join-Path $SitePath $_ } | Where-Object { Test-Path -LiteralPath $_ }
    foreach ($file in @($folders | ForEach-Object { Get-ChildItem -LiteralPath $_ -File -Recurse })) {
        if ((Get-Content -LiteralPath $file.FullName -Raw).Contains($resourcePath)) { return $true }
    }
    return $false
}

<#
.SYNOPSIS
    Overwrites each of tetron.io's JIM screenshots with the capture's, once all of them have been checked.
.OUTPUTS
    An object whose Published property lists the site paths written.
#>
function Update-TetronSiteImage {
    param(
        [Parameter(Mandatory)][string]$SitePath,
        [Parameter(Mandatory)][string]$ScreenshotPath,
        [System.Collections.IDictionary]$Images = $script:TetronSiteImages
    )

    # Every check before any write, so a refused refresh leaves the checkout as it found it.
    foreach ($sitePathKey in $Images.Keys) {
        $source = Join-Path $ScreenshotPath $Images[$sitePathKey]
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The capture has no $($Images[$sitePathKey]) in $ScreenshotPath."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $SitePath $sitePathKey) -PathType Leaf)) {
            throw "tetron.io has no $sitePathKey any more; update TetronAssets.ps1 to where the site keeps its JIM screenshot."
        }
        if (-not (Test-TetronSiteImageReferenced -SitePath $SitePath -ImagePath $sitePathKey)) {
            throw "tetron.io has no page that shows $($sitePathKey -replace '^assets/', ''); update TetronAssets.ps1 to the image the site now uses."
        }
    }

    $published = foreach ($sitePathKey in $Images.Keys) {
        Copy-Item -LiteralPath (Join-Path $ScreenshotPath $Images[$sitePathKey]) -Destination (Join-Path $SitePath $sitePathKey) -Force
        $sitePathKey
    }
    return [pscustomobject]@{ Published = @($published) }
}
