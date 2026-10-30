# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Pester tests for the anonymous-pull check's place in release.yml: it runs after the images are pushed, on every
    image the release pushes, and the GitHub Release is not created until it passes. The check itself asks ghcr.io, so
    it is exercised by the release, not here.
#>

BeforeAll {
    $script:Release = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows' 'release.yml') -Raw
    $script:Job = [regex]::Match($script:Release, '(?ms)^  check-images-public:.*?(?=^  \S|\z)').Value
}

Describe 'the anonymous-pull check in release.yml' {
    It 'is a job that runs once the images are pushed' {
        $script:Job | Should -Not -BeNullOrEmpty
        $script:Job | Should -Match '(?m)^    needs: \[?[^\n]*\bbuild-containers\b'
    }

    It 'checks every image the release pushes' {
        $pushed = [regex]::Matches(
            [regex]::Match($script:Release, '(?ms)^  build-containers:.*?(?=^  \S)').Value,
            '(?m)^\s+- image: (\S+)') | ForEach-Object { $_.Groups[1].Value }
        $pushed | Should -Not -BeNullOrEmpty
        foreach ($image in $pushed) {
            $script:Job | Should -Match "/$([regex]::Escape($image))\b"
        }
        $script:Job | Should -Match 'Test-PublicImage\.ps1'
    }

    It 'holds back the GitHub Release until it passes' {
        $script:Release | Should -Match '(?ms)^  create-release:.*?^    needs: \[?[^\n]*\bcheck-images-public\b'
    }
}
