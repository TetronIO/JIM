# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/screenshots/TetronAssets.ps1.

.DESCRIPTION
    tetron.io's homepage shows JIM's home page; a release refreshes that image from its screenshot library (#582).
    These tests build a small site and capture under TestDrive.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'screenshots' 'TetronAssets.ps1')

    function New-TetronSite {
        param([switch]$WithoutReference, [switch]$WithoutImage)

        $site = Join-Path $TestDrive "site-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path (Join-Path $site 'content'), (Join-Path $site 'assets/images') -Force | Out-Null
        $hero = if ($WithoutReference) { '<p>No screenshot here.</p>' } else { '{{< resized-figure src="images/homepage-dark.png" caption="JIM" >}}' }
        Set-Content -LiteralPath (Join-Path $site 'content/_index.md') -Value $hero
        if (-not $WithoutImage) {
            [System.IO.File]::WriteAllBytes((Join-Path $site 'assets/images/homepage-dark.png'), [byte[]](1, 1, 1))
        }
        return $site
    }

    function New-Capture {
        param([switch]$WithoutHomepage)

        $capture = Join-Path $TestDrive "capture-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $capture -Force | Out-Null
        if (-not $WithoutHomepage) {
            [System.IO.File]::WriteAllBytes((Join-Path $capture 'homepage-dark.png'), [byte[]](2, 2, 2))
        }
        [System.IO.File]::WriteAllBytes((Join-Path $capture 'homepage-light.png'), [byte[]](3, 3, 3))
        return $capture
    }
}

Describe 'Update-TetronSiteImage' {
    It 'replaces the homepage image with the capture''s' {
        $site = New-TetronSite
        $capture = New-Capture

        $result = Update-TetronSiteImage -SitePath $site -ScreenshotPath $capture

        [System.IO.File]::ReadAllBytes((Join-Path $site 'assets/images/homepage-dark.png')) | Should -Be ([byte[]](2, 2, 2))
        $result.Published | Should -Be @('assets/images/homepage-dark.png')
    }

    It 'refuses a capture without the image, and changes nothing' {
        $site = New-TetronSite
        $capture = New-Capture -WithoutHomepage

        { Update-TetronSiteImage -SitePath $site -ScreenshotPath $capture } | Should -Throw '*homepage-dark.png*'
        [System.IO.File]::ReadAllBytes((Join-Path $site 'assets/images/homepage-dark.png')) | Should -Be ([byte[]](1, 1, 1))
    }

    # A redesign that drops or moves the screenshot should stop the publish, not have each release commit a file
    # nothing on the site shows.
    It 'refuses a site whose pages no longer show the image' {
        $site = New-TetronSite -WithoutReference
        $capture = New-Capture

        { Update-TetronSiteImage -SitePath $site -ScreenshotPath $capture } | Should -Throw '*no page*images/homepage-dark.png*'
        [System.IO.File]::ReadAllBytes((Join-Path $site 'assets/images/homepage-dark.png')) | Should -Be ([byte[]](1, 1, 1))
    }

    It 'refuses a site that no longer has the image file' {
        $site = New-TetronSite -WithoutImage
        $capture = New-Capture

        { Update-TetronSiteImage -SitePath $site -ScreenshotPath $capture } | Should -Throw '*assets/images/homepage-dark.png*'
        Test-Path -LiteralPath (Join-Path $site 'assets/images/homepage-dark.png') | Should -BeFalse
    }
}
