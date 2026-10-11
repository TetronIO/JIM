# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/screenshots/ScreenshotManifest.ps1.

.DESCRIPTION
    The screenshot manifest is the list of published screenshots that Capture-Screenshots.ps1 produces (#582). A
    mistake in it should stop the run before a browser starts, naming the entry at fault, and a capture that comes out
    at any size other than its profile's should fail the run rather than be published.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'screenshots' 'ScreenshotManifest.ps1')
    $script:RepositoryManifest = Join-Path $PSScriptRoot '..' 'screenshots' 'manifest.json'

    # A valid manifest with one portal entry, as a hashtable so each test can break one thing.
    function New-Manifest {
        @{
            profiles    = @{
                portal   = @{ viewport = @{ width = 1674; height = 1046 }; deviceScaleFactor = 2 }
                terminal = @{ viewport = @{ width = 972; height = 611 }; deviceScaleFactor = 2; transparent = $true }
            }
            screenshots = @(
                @{
                    name    = 'connected-systems-list'
                    kind    = 'portal'
                    profile = 'portal'
                    themes  = @('dark', 'light')
                    steps   = @(@{ goto = '/admin/connected-systems' }, @{ waitFor = @{ role = 'button'; name = 'Create Connected System' } })
                }
            )
        }
    }

    function ConvertTo-ManifestJson([hashtable]$Manifest) { $Manifest | ConvertTo-Json -Depth 10 }

    # The smallest file Get-PngDimensions needs: the signature and an IHDR chunk.
    function New-PngHeader([int]$Width, [int]$Height) {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "$([Guid]::NewGuid().ToString('N')).png"
        $bytes = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52) +
            [BitConverter]::GetBytes([System.Buffers.Binary.BinaryPrimitives]::ReverseEndianness([int]$Width)) +
            [BitConverter]::GetBytes([System.Buffers.Binary.BinaryPrimitives]::ReverseEndianness([int]$Height)) +
            [byte[]](8, 6, 0, 0, 0, 0, 0, 0, 0)
        [System.IO.File]::WriteAllBytes($path, $bytes)
        $path
    }
}

Describe 'ConvertFrom-ScreenshotManifest' {
    It 'reads a valid manifest and works out each profile''s image size from its viewport and density' {
        $manifest = ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson (New-Manifest))

        $manifest.Screenshots.Count | Should -Be 1
        $manifest.Profiles['portal'].ImageWidth | Should -Be 3348
        $manifest.Profiles['portal'].ImageHeight | Should -Be 2092
        $manifest.Profiles['terminal'].Transparent | Should -BeTrue
    }

    It 'rejects a manifest with no screenshots' {
        $m = New-Manifest; $m.screenshots = @()
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw '*no screenshots*'
    }

    It 'rejects two entries with the same name, naming it' {
        $m = New-Manifest; $m.screenshots += $m.screenshots[0]
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw "*connected-systems-list*more than once*"
    }

    It 'rejects a name that is not a lower-case file name' -ForEach @(
        @{ Name = 'Connected Systems' }, @{ Name = 'connected_systems' }, @{ Name = '' }, @{ Name = '-leading' }
    ) {
        $m = New-Manifest; $m.screenshots[0].name = $Name
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw '*name*'
    }

    It 'rejects an unknown profile, naming the entry and the profile' {
        $m = New-Manifest; $m.screenshots[0].profile = 'tablet'
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw "*connected-systems-list*'tablet'*"
    }

    It 'rejects an unknown kind' {
        $m = New-Manifest; $m.screenshots[0].kind = 'video'
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw "*connected-systems-list*'video'*"
    }

    It 'rejects <Case>' -ForEach @(
        @{ Case = 'an unknown theme'; Themes = @('dark', 'sepia') }
        @{ Case = 'no themes'; Themes = @() }
        @{ Case = 'a repeated theme'; Themes = @('dark', 'dark') }
    ) {
        $m = New-Manifest; $m.screenshots[0].themes = $Themes
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw '*connected-systems-list*theme*'
    }

    It 'rejects a profile whose viewport or density is not positive' {
        $m = New-Manifest; $m.profiles.portal.deviceScaleFactor = 0
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw "*profile 'portal'*"
    }

    It 'rejects a portal entry with no steps' {
        $m = New-Manifest; $m.screenshots[0].steps = @()
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw '*connected-systems-list*steps*'
    }

    It 'rejects <Case>, naming the entry' -ForEach @(
        @{ Case = 'a step of an unknown shape'; Step = @{ drag = '/x' } }
        @{ Case = 'a step with two actions'; Step = @{ goto = '/a'; click = @{ text = 'B' } } }
        @{ Case = 'a goto that is not a path on the portal'; Step = @{ goto = 'https://example.com/' } }
        @{ Case = 'a click with no locator'; Step = @{ click = @{} } }
        @{ Case = 'a locator naming two ways to find the element'; Step = @{ click = @{ text = 'A'; selector = '.b' } } }
        @{ Case = 'a role locator with no name'; Step = @{ click = @{ role = 'tab' } } }
        @{ Case = 'a wait that is not a positive number of milliseconds'; Step = @{ wait = -5 } }
        @{ Case = 'a fill with nothing to type'; Step = @{ fill = @{ selector = 'input' } } }
        @{ Case = 'a fill typing something other than text'; Step = @{ fill = @{ selector = 'input'; value = 5 } } }
        @{ Case = 'a value on an action that types nothing'; Step = @{ click = @{ text = 'Save'; value = 'x' } } }
    ) {
        $m = New-Manifest; $m.screenshots[0].steps = @($Step)
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw '*connected-systems-list*step 1*'
    }

    It 'accepts every step shape the capture understands' {
        $m = New-Manifest
        $m.screenshots[0].steps = @(
            @{ goto = '/admin/operations?t=history' }
            @{ click = @{ text = 'Yellowstone APAC' } }
            @{ click = @{ role = 'tab'; name = 'Schema' } }
            @{ waitFor = @{ selector = '.mud-table-row' } }
            @{ scroll = @{ text = 'Attribute Flow' } }
            @{ fill = @{ selector = 'input[placeholder^="Search"]'; value = 'Company-NexusDynamics' } }
            @{ wait = 500 }
        )
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Not -Throw
    }

    It 'rejects a terminal entry with no commands to show' {
        $m = New-Manifest
        $m.screenshots[0] = @{ name = 'powershell-1'; kind = 'terminal'; profile = 'terminal'; themes = @('dark'); commands = @('!Connect-JIM') }
        { ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson $m) } | Should -Throw '*powershell-1*command*'
    }
}

Describe 'Get-ScreenshotFileNames' {
    It 'suffixes the theme when an image is published in more than one' {
        $entry = [pscustomobject]@{ name = 'homepage'; themes = @('dark', 'light') }
        $files = @(Get-ScreenshotFileNames -Screenshot $entry)
        $files.Theme | Should -Be @('dark', 'light')
        $files.FileName | Should -Be @('homepage-dark.png', 'homepage-light.png')
    }

    It 'uses the bare name when an image is published in one theme only, as junctional.io names it' {
        $entry = [pscustomobject]@{ name = 'api-documentation'; themes = @('dark') }
        (Get-ScreenshotFileNames -Screenshot $entry).FileName | Should -Be 'api-documentation.png'
    }
}

Describe 'Get-PngDimensions' {
    It 'reads the width and height from the image header' {
        $path = New-PngHeader -Width 3348 -Height 2092
        try {
            $size = Get-PngDimensions -Path $path
            $size.Width | Should -Be 3348
            $size.Height | Should -Be 2092
        }
        finally { Remove-Item -LiteralPath $path }
    }

    It 'refuses a file that is not a PNG' {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "$([Guid]::NewGuid().ToString('N')).png"
        Set-Content -LiteralPath $path -Value 'not an image'
        try { { Get-PngDimensions -Path $path } | Should -Throw '*not a PNG*' }
        finally { Remove-Item -LiteralPath $path }
    }
}

Describe 'Find-ScreenshotSizeMismatch' {
    BeforeAll {
        $script:Profiles = (ConvertFrom-ScreenshotManifest -Json (ConvertTo-ManifestJson (New-Manifest))).Profiles
    }

    It 'passes an image at exactly its profile''s size' {
        $path = New-PngHeader -Width 3348 -Height 2092
        try { Find-ScreenshotSizeMismatch -Path $path -ProfileName 'portal' -Profiles $script:Profiles | Should -BeNullOrEmpty }
        finally { Remove-Item -LiteralPath $path }
    }

    It 'reports an image at any other size, with both sizes' {
        $path = New-PngHeader -Width 1440 -Height 900
        try {
            Find-ScreenshotSizeMismatch -Path $path -ProfileName 'portal' -Profiles $script:Profiles |
                Should -BeLike '*1440x900*expected 3348x2092*'
        }
        finally { Remove-Item -LiteralPath $path }
    }
}

Describe 'The repository''s screenshot manifest' {
    It 'is valid' {
        { Read-ScreenshotManifest -Path $script:RepositoryManifest } | Should -Not -Throw
    }

    It 'publishes every portal screenshot at the tour''s 3348x2092' {
        $manifest = Read-ScreenshotManifest -Path $script:RepositoryManifest
        $manifest.Profiles['portal'].ImageWidth | Should -Be 3348
        $manifest.Profiles['portal'].ImageHeight | Should -Be 2092
    }
}
