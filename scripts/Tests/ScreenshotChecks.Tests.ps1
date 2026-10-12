# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/screenshots/ScreenshotChecks.ps1.

.DESCRIPTION
    A release publishes its screenshots to junctional.io and tetron.io with no one looking at them first (#582), so the
    capture refuses an image that is blank or a dark and light pair that came out in the same theme. These tests build
    small PNGs and also measure real ones from the portal, whose figures were taken with Pillow's luminance
    (ITU-R 601-2), so the decoder's row filters are checked against an independent implementation.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'screenshots' 'ScreenshotChecks.ps1')

    # An 8-bit RGB PNG of the given rows of [r, g, b] pixels, each row filtered with None. The checks ignore chunk
    # CRCs, so they are left zero here.
    function New-TestPng {
        param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][object[]]$Rows)

        $height = $Rows.Count
        $width = $Rows[0].Count
        $raw = [System.IO.MemoryStream]::new()
        foreach ($row in $Rows) {
            $raw.WriteByte(0)
            foreach ($pixel in $row) { $raw.Write([byte[]]$pixel, 0, 3) }
        }
        $compressed = [System.IO.MemoryStream]::new()
        $zlib = [System.IO.Compression.ZLibStream]::new($compressed, [System.IO.Compression.CompressionLevel]::Optimal, $true)
        $zlib.Write($raw.ToArray(), 0, [int]$raw.Length)
        $zlib.Dispose()

        $file = [System.IO.MemoryStream]::new()
        $file.Write([byte[]](137, 80, 78, 71, 13, 10, 26, 10), 0, 8)
        $chunk = {
            param([string]$Type, [byte[]]$Data)
            $length = [BitConverter]::GetBytes([uint32]$Data.Length); [Array]::Reverse($length)
            $file.Write($length, 0, 4)
            $file.Write([Text.Encoding]::ASCII.GetBytes($Type), 0, 4)
            $file.Write($Data, 0, $Data.Length)
            $file.Write([byte[]](0, 0, 0, 0), 0, 4)
        }
        $w = [BitConverter]::GetBytes([uint32]$width); [Array]::Reverse($w)
        $h = [BitConverter]::GetBytes([uint32]$height); [Array]::Reverse($h)
        & $chunk 'IHDR' ([byte[]]($w + $h + [byte[]](8, 2, 0, 0, 0)))
        & $chunk 'IDAT' $compressed.ToArray()
        & $chunk 'IEND' ([byte[]]@())
        [System.IO.File]::WriteAllBytes($Path, $file.ToArray())
    }

    function New-UniformPng([string]$Path, [byte[]]$Colour) {
        $row = @(1..8 | ForEach-Object { , $Colour })
        New-TestPng -Path $Path -Rows @(1..8 | ForEach-Object { , $row })
    }

    $script:wwwroot = Join-Path $PSScriptRoot '..' '..' 'src' 'JIM.Web' 'wwwroot'
}

Describe 'Get-PngLuminance' {
    It 'measures a uniform image as having no contrast' {
        $path = Join-Path $TestDrive 'white.png'
        New-UniformPng $path ([byte[]](255, 255, 255))

        $stats = Get-PngLuminance -Path $path

        $stats.Mean | Should -BeGreaterThan 254.9
        $stats.StdDev | Should -BeLessThan 0.01
    }

    It 'measures an image half black and half white' {
        $path = Join-Path $TestDrive 'halves.png'
        $black = @(1..4 | ForEach-Object { , [byte[]](0, 0, 0) })
        $white = @(1..4 | ForEach-Object { , [byte[]](255, 255, 255) })
        New-TestPng -Path $path -Rows @(1..8 | ForEach-Object { , ($black + $white) })

        $stats = Get-PngLuminance -Path $path

        $stats.Mean | Should -BeGreaterThan 127.4
        $stats.Mean | Should -BeLessThan 127.6
        $stats.StdDev | Should -BeGreaterThan 127.4
        $stats.StdDev | Should -BeLessThan 127.6
    }

    # Real encoders choose a filter per row (Sub, Up, Average, Paeth); these are RGBA, 8 bits per channel.
    It 'decodes <Name> to the luminance Pillow measures' -ForEach @(
        @{ Name = 'favicon-16x16.png'; Mean = 76.7422; StdDev = 76.1233 }
        @{ Name = 'apple-touch-icon.png'; Mean = 53.9009; StdDev = 73.1929 }
        @{ Name = 'android-chrome-512x512.png'; Mean = 51.6415; StdDev = 72.9679 }
    ) {
        $stats = Get-PngLuminance -Path (Join-Path $script:wwwroot $Name)

        # Pillow rounds each pixel's luminance to a whole number; this keeps the fraction.
        [Math]::Abs($stats.Mean - $Mean) | Should -BeLessThan 0.6
        [Math]::Abs($stats.StdDev - $StdDev) | Should -BeLessThan 0.6
    }

    It 'refuses a file that is not a PNG' {
        $path = Join-Path $TestDrive 'not.png'
        Set-Content -LiteralPath $path -Value 'hello'

        { Get-PngLuminance -Path $path } | Should -Throw '*not a PNG*'
    }
}

Describe 'Find-ScreenshotContentProblem' {
    BeforeEach {
        $script:folder = Join-Path $TestDrive "library-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:folder | Out-Null
    }

    It 'passes a dark and light pair that differ, and an image with content' {
        New-UniformPng (Join-Path $script:folder 'page-dark.png') ([byte[]](20, 24, 30))
        New-UniformPng (Join-Path $script:folder 'page-light.png') ([byte[]](240, 240, 240))
        $black = @(1..4 | ForEach-Object { , [byte[]](0, 0, 0) })
        $white = @(1..4 | ForEach-Object { , [byte[]](255, 255, 255) })
        New-TestPng -Path (Join-Path $script:folder 'terminal.png') -Rows @(1..8 | ForEach-Object { , ($black + $white) })

        $problems = @(Find-ScreenshotContentProblem -Path @(Get-ChildItem $script:folder -Filter *.png | ForEach-Object FullName) -BlankThreshold 0)

        $problems | Should -BeNullOrEmpty
    }

    It 'reports an image with almost no contrast as blank' {
        New-UniformPng (Join-Path $script:folder 'empty.png') ([byte[]](255, 255, 255))

        $problems = @(Find-ScreenshotContentProblem -Path (Join-Path $script:folder 'empty.png'))

        $problems.Count | Should -Be 1
        $problems[0] | Should -BeLike 'empty.png*blank*'
    }

    It 'reports a dark and light pair that came out in the same theme' {
        New-UniformPng (Join-Path $script:folder 'page-dark.png') ([byte[]](240, 240, 240))
        New-UniformPng (Join-Path $script:folder 'page-light.png') ([byte[]](240, 240, 240))

        $problems = @(Find-ScreenshotContentProblem -Path @(Get-ChildItem $script:folder -Filter *.png | ForEach-Object FullName) -BlankThreshold 0)

        $problems.Count | Should -Be 1
        $problems[0] | Should -BeLike 'page-dark.png and page-light.png*same theme*'
    }
}
