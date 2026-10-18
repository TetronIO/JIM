# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/screenshots/TourAssets.ps1.

.DESCRIPTION
    A release refreshes junctional.io's product tour from the captured screenshots (#582): the images the tour shows
    go into a new src/assets/tour/<version>/ folder, both dictionaries' imageVersion move to it, and folders no step
    uses any more are removed. These tests build a small stand-in of the site in TestDrive.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'screenshots' 'TourAssets.ps1')

    # A dictionary in the shape of junctional.io's src/i18n/en-GB.ts: a tour-wide imageVersion, steps with a
    # dark-and-light pair, one with a single image, and one pinned to an older version by its own image.version.
    function New-DictionaryText {
        param([string]$ImageVersion = 'v0.10.3', [string]$PinnedVersion = 'v0.10.2', [string]$Spelling = 'Synchronisation')
        @"
export const enGB: Dictionary = {
  roadmap: {
    milestones: [
      { version: "v1.0", title: "One" },
    ],
  },
  tour: {
    imageVersion: "$ImageVersion",
    steps: [
      {
        slug: "connected-systems",
        alt: "Connected Systems list view.",
        image: {
          dark: "connected-systems-list-dark.png",
          light: "connected-systems-list-light.png",
        },
      },
      {
        slug: "sync-rules",
        alt: "$Spelling Rules list view.",
        image: { dark: "synchronisation-rules-list-dark.png", light: "synchronisation-rules-list-light.png" },
      },
      {
        slug: "powershell",
        image: { dark: "powershell-1.png" },
      },
      {
        slug: "homepage",
        image: { dark: "homepage-dark.png", light: "homepage-light.png", version: "$PinnedVersion" },
      },
    ],
  },
};
"@
    }

    function New-TourSite {
        param([string]$Root, [string]$PinnedVersion = 'v0.10.2')
        $i18n = Join-Path $Root 'src' 'i18n'
        New-Item -ItemType Directory -Path $i18n -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $i18n 'en-GB.ts') -Value (New-DictionaryText -PinnedVersion $PinnedVersion) -NoNewline
        Set-Content -LiteralPath (Join-Path $i18n 'en-US.ts') -Value (New-DictionaryText -PinnedVersion $PinnedVersion -Spelling 'Synchronization') -NoNewline
        foreach ($version in @('v0.10.0', 'v0.10.2', 'v0.10.3')) {
            $folder = Join-Path $Root 'src' 'assets' 'tour' $version
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $folder 'homepage-dark.png') -Value $version
        }
    }

    function New-ScreenshotFolder {
        param([string]$Path, [string[]]$Omit = @())
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        $names = @(
            'connected-systems-list-dark.png', 'connected-systems-list-light.png',
            'synchronisation-rules-list-dark.png', 'synchronisation-rules-list-light.png',
            'powershell-1.png', 'homepage-dark.png', 'homepage-light.png',
            'logs-dark.png', 'logs-light.png'
        )
        foreach ($name in $names | Where-Object { $_ -notin $Omit }) {
            Set-Content -LiteralPath (Join-Path $Path $name) -Value "new $name"
        }
    }
}

Describe 'Get-TourImageVersion' {
    It 'returns the tour-wide image version' {
        Get-TourImageVersion -Text (New-DictionaryText -ImageVersion 'v0.12.0') | Should -Be 'v0.12.0'
    }

    It 'refuses a dictionary with no imageVersion' {
        { Get-TourImageVersion -Text 'export const enGB = {};' } | Should -Throw '*imageVersion*'
    }

    It 'refuses a dictionary with more than one imageVersion' {
        $text = (New-DictionaryText) + "`nconst again = { imageVersion: ""v0.9"" };"
        { Get-TourImageVersion -Text $text } | Should -Throw '*imageVersion*'
    }
}

Describe 'Set-TourImageVersion' {
    It 'changes the imageVersion value and nothing else' {
        $text = New-DictionaryText -ImageVersion 'v0.10.3'

        $updated = Set-TourImageVersion -Text $text -Version 'v0.17.0'

        $updated | Should -Be ($text.Replace('imageVersion: "v0.10.3"', 'imageVersion: "v0.17.0"'))
    }

    It 'leaves a step pinned to its own version alone' {
        $updated = Set-TourImageVersion -Text (New-DictionaryText -PinnedVersion 'v0.10.2') -Version 'v0.17.0'

        $updated | Should -Match ([regex]::Escape('version: "v0.10.2"'))
    }

    It 'refuses a version that is not a release folder name' {
        { Set-TourImageVersion -Text (New-DictionaryText) -Version '0.17.0"; evil' } | Should -Throw '*version*'
    }
}

Describe 'Get-TourImageReference' {
    It 'returns every image a step shows, with the version a step pins' {
        $references = Get-TourImageReference -Text (New-DictionaryText -PinnedVersion 'v0.10.2')

        $references.Count | Should -Be 7
        ($references | Where-Object FileName -eq 'powershell-1.png').Version | Should -BeNullOrEmpty
        ($references | Where-Object FileName -eq 'homepage-light.png').Version | Should -Be 'v0.10.2'
        ($references | Where-Object FileName -eq 'synchronisation-rules-list-dark.png') | Should -Not -BeNullOrEmpty
    }

    It 'ignores version fields outside a step image, such as the roadmap''s' {
        $references = Get-TourImageReference -Text (New-DictionaryText)

        $references.Version | Should -Not -Contain 'v1.0'
    }
}

Describe 'Update-TourAssetFolder' {
    BeforeEach {
        $site = Join-Path $TestDrive ([guid]::NewGuid().ToString('n'))
        $shots = Join-Path $TestDrive ([guid]::NewGuid().ToString('n'))
        New-TourSite -Root $site
        New-ScreenshotFolder -Path $shots
    }

    It 'copies only the images the tour uses into the new version folder' {
        Update-TourAssetFolder -SitePath $site -ScreenshotPath $shots -Version 'v0.17.0' | Out-Null

        $copied = @(Get-ChildItem -LiteralPath (Join-Path $site 'src' 'assets' 'tour' 'v0.17.0') -File | ForEach-Object Name | Sort-Object)
        $copied | Should -Be @(
            'connected-systems-list-dark.png', 'connected-systems-list-light.png', 'powershell-1.png',
            'synchronisation-rules-list-dark.png', 'synchronisation-rules-list-light.png'
        )
    }

    It 'moves both dictionaries to the new version' {
        Update-TourAssetFolder -SitePath $site -ScreenshotPath $shots -Version 'v0.17.0' | Out-Null

        foreach ($file in @('en-GB.ts', 'en-US.ts')) {
            $text = Get-Content -LiteralPath (Join-Path $site 'src' 'i18n' $file) -Raw
            Get-TourImageVersion -Text $text | Should -Be 'v0.17.0'
        }
        Get-Content -LiteralPath (Join-Path $site 'src' 'i18n' 'en-US.ts') -Raw | Should -Match 'Synchronization Rules'
    }

    It 'removes folders no step uses and keeps the one a step pins' {
        $result = Update-TourAssetFolder -SitePath $site -ScreenshotPath $shots -Version 'v0.17.0'

        $folders = @(Get-ChildItem -LiteralPath (Join-Path $site 'src' 'assets' 'tour') -Directory | ForEach-Object Name | Sort-Object)
        $folders | Should -Be @('v0.10.2', 'v0.17.0')
        @($result.RemovedFolders | Sort-Object) | Should -Be @('v0.10.0', 'v0.10.3')
        $result.PreviousVersion | Should -Be 'v0.10.3'
    }

    It 'writes the homepage images where links from outside the site can rely on them' {
        $result = Update-TourAssetFolder -SitePath $site -ScreenshotPath $shots -Version 'v0.17.0'

        $stable = Join-Path $site 'public' 'assets' 'screenshots'
        Get-Content -LiteralPath (Join-Path $stable 'homepage-dark.png') -Raw | Should -Match 'new homepage-dark.png'
        Get-Content -LiteralPath (Join-Path $stable 'homepage-light.png') -Raw | Should -Match 'new homepage-light.png'
        @($result.Stable | Sort-Object) | Should -Be @('homepage-dark.png', 'homepage-light.png')
    }

    It 'refuses a library without the homepage images the stable links need' {
        $partial = Join-Path $TestDrive ([guid]::NewGuid().ToString('n'))
        New-ScreenshotFolder -Path $partial -Omit 'homepage-light.png'

        { Update-TourAssetFolder -SitePath $site -ScreenshotPath $partial -Version 'v0.17.0' } | Should -Throw '*homepage-light.png*'
    }

    It 'names a missing image and leaves the site untouched' {
        $partial = Join-Path $TestDrive ([guid]::NewGuid().ToString('n'))
        New-ScreenshotFolder -Path $partial -Omit 'powershell-1.png'

        { Update-TourAssetFolder -SitePath $site -ScreenshotPath $partial -Version 'v0.17.0' } | Should -Throw '*powershell-1.png*'

        Test-Path -LiteralPath (Join-Path $site 'src' 'assets' 'tour' 'v0.17.0') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $site 'src' 'assets' 'tour' 'v0.10.3') | Should -BeTrue
        Get-TourImageVersion -Text (Get-Content -LiteralPath (Join-Path $site 'src' 'i18n' 'en-GB.ts') -Raw) | Should -Be 'v0.10.3'
    }

    It 'refuses dictionaries that show different images' {
        $usPath = Join-Path $site 'src' 'i18n' 'en-US.ts'
        (Get-Content -LiteralPath $usPath -Raw).Replace('powershell-1.png', 'powershell-2.png') | Set-Content -LiteralPath $usPath -NoNewline

        { Update-TourAssetFolder -SitePath $site -ScreenshotPath $shots -Version 'v0.17.0' } | Should -Throw '*en-US*'
    }

    It 'replaces the folder when run again for the same version' {
        Update-TourAssetFolder -SitePath $site -ScreenshotPath $shots -Version 'v0.17.0' | Out-Null
        $stray = Join-Path $site 'src' 'assets' 'tour' 'v0.17.0' 'stray.png'
        Set-Content -LiteralPath $stray -Value 'stray'

        Update-TourAssetFolder -SitePath $site -ScreenshotPath $shots -Version 'v0.17.0' | Out-Null

        Test-Path -LiteralPath $stray | Should -BeFalse
    }
}

# A release publishes the refresh with no one looking first, so the site must build with it before anything is
# committed. npm is replaced with a recorder here.
Describe 'Invoke-TourSiteBuild' {
    BeforeAll {
        # Pester can only mock a command that exists, and the self-hosted runners have no npm. This stand-in gives
        # every Mock below something to replace, so the tests behave the same with or without npm installed.
        function npm { throw 'npm must be mocked in these tests' }
    }

    BeforeEach {
        $script:npmCalls = [System.Collections.Generic.List[string]]::new()
        $script:site = Join-Path $TestDrive "site-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:site | Out-Null
    }

    It 'installs the locked dependencies, then builds, in the site' {
        Mock npm { $script:npmCalls.Add("$((Get-Location).ProviderPath)|$($args -join ' ')"); $global:LASTEXITCODE = 0 }

        Invoke-TourSiteBuild -SitePath $script:site

        $script:npmCalls.Count | Should -Be 2
        $script:npmCalls[0] | Should -BeLike "$($script:site)|ci*"
        $script:npmCalls[1] | Should -BeLike "$($script:site)|run build"
    }

    It 'refuses a refresh the site does not build with' {
        Mock npm { $global:LASTEXITCODE = if ($args[0] -eq 'run') { 1 } else { 0 } }

        { Invoke-TourSiteBuild -SitePath $script:site } | Should -Throw '*does not build*'
    }

    It 'stops when the dependencies do not install' {
        Mock npm { $script:npmCalls.Add($args -join ' '); $global:LASTEXITCODE = 1 }

        { Invoke-TourSiteBuild -SitePath $script:site } | Should -Throw '*npm ci*'
        $script:npmCalls.Count | Should -Be 1
    }
}
