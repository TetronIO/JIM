# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/screenshots/ReadmeImageCache.ps1.

.DESCRIPTION
    GitHub shows the README's screenshots through its image proxy, which kept v0.17.0's homepage images after v0.18.0
    published new ones, because junctional.io sent no caching headers and the proxy's default is a year (#582). After a
    release publishes its screenshots, the proxy's copies are purged once the site serves the new images. These tests
    replace the network with recorders.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'screenshots' 'ReadmeImageCache.ps1')

    function ConvertTo-ProxyUrl([string]$Origin) {
        $hex = -join ([System.Text.Encoding]::UTF8.GetBytes($Origin) | ForEach-Object { $_.ToString('x2') })
        "https://camo.githubusercontent.com/0123abcd/$hex"
    }
    function ConvertTo-Bytes([string]$Text) { [System.Text.Encoding]::UTF8.GetBytes($Text) }

    $script:light = 'https://junctional.io/assets/screenshots/homepage-light.png'
    $script:dark = 'https://junctional.io/assets/screenshots/homepage-dark.png'
    $script:logo = 'https://junctional.io/assets/jim-logo.png'
    $script:readme = @"
<img src="$(ConvertTo-ProxyUrl $logo)" data-canonical-src="$logo">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="$(ConvertTo-ProxyUrl $dark)">
  <img src="$(ConvertTo-ProxyUrl $light)" data-canonical-src="$light">
</picture>
<img src="$(ConvertTo-ProxyUrl $light)">
"@
}

Describe 'Get-ReadmeProxiedImage' {
    It 'finds each proxied screenshot once, with the address it stands for' {
        $images = @(Get-ReadmeProxiedImage -Html $readme)

        $images.Count | Should -Be 2
        ($images.Origin | Sort-Object) | Should -Be @($dark, $light)
        ($images | Where-Object Origin -eq $light).ProxyUrl | Should -Be (ConvertTo-ProxyUrl $light)
    }

    It 'leaves out proxied images outside the screenshots folder' {
        @(Get-ReadmeProxiedImage -Html $readme).Origin | Should -Not -Contain $logo
    }
}

Describe 'Clear-ReadmeImageCache' {
    BeforeEach {
        $script:siteServes = @{ $light = 'new-light'; $dark = 'new-dark' }
        $script:proxyServes = @{ (ConvertTo-ProxyUrl $light) = 'old-light'; (ConvertTo-ProxyUrl $dark) = 'old-dark' }
        $script:purged = [System.Collections.Generic.List[string]]::new()

        # The site's repository holds what a publish wrote.
        Mock Invoke-GitHubApi {
            $name = if ($Path -match 'homepage-dark') { 'new-dark' } else { 'new-light' }
            [pscustomobject]@{ content = [Convert]::ToBase64String((ConvertTo-Bytes $name)) }
        }
        Mock Get-UrlBytes {
            if ($script:siteServes.ContainsKey($Uri)) { return , (ConvertTo-Bytes $script:siteServes[$Uri]) }
            return , (ConvertTo-Bytes $script:proxyServes[$Uri])
        }
        # A purge makes the proxy fetch the site again.
        Mock Send-ProxyPurge {
            $script:purged.Add($Uri)
            $origin = ($script:siteServes.Keys | Where-Object { (ConvertTo-ProxyUrl $_) -eq $Uri })
            $script:proxyServes[$Uri] = $script:siteServes[$origin]
        }
        Mock Start-Sleep {}
    }

    It 'purges each image once the site serves what its repository holds' {
        $results = @(Clear-ReadmeImageCache -Html $readme -SiteRepository 'TetronIO/junctional.io' -Token 't')

        $results.Status | Should -Be @('Cleared', 'Cleared')
        $purged | Sort-Object | Should -Be @((ConvertTo-ProxyUrl $dark), (ConvertTo-ProxyUrl $light) | Sort-Object)
    }

    It 'reads the image the site serves from public/ in its repository' {
        Clear-ReadmeImageCache -Html $readme -SiteRepository 'TetronIO/junctional.io' -Token 't' | Out-Null

        Should -Invoke Invoke-GitHubApi -ParameterFilter {
            $Path -eq 'repos/TetronIO/junctional.io/contents/public/assets/screenshots/homepage-light.png?ref=main'
        }
    }

    # The site deploys a few minutes after its pull request merges; purging before then caches the old image again.
    It 'waits for the site to serve the new image before purging' {
        $script:siteServes[$light] = 'old-light'
        $script:polls = 0
        Mock Start-Sleep { $script:polls++; if ($script:polls -ge 2) { $script:siteServes[$light] = 'new-light' } }

        $results = @(Clear-ReadmeImageCache -Html $readme -SiteRepository 'TetronIO/junctional.io' -Token 't' -PollSeconds 1)

        $results.Status | Should -Be @('Cleared', 'Cleared')
        $polls | Should -BeGreaterOrEqual 2
    }

    It 'reports an image the site never starts serving, without purging it' {
        $script:siteServes[$light] = 'old-light'

        $results = @(Clear-ReadmeImageCache -Html $readme -SiteRepository 'TetronIO/junctional.io' -Token 't' -TimeoutSeconds 0)

        ($results | Where-Object Origin -eq $light).Status | Should -Be 'SiteStale'
        $purged | Should -Not -Contain (ConvertTo-ProxyUrl $light)
        ($results | Where-Object Origin -eq $dark).Status | Should -Be 'Cleared'
    }

    It 'reports a proxy still serving the old image after the purge' {
        Mock Send-ProxyPurge { $script:purged.Add($Uri) }

        $results = @(Clear-ReadmeImageCache -Html $readme -SiteRepository 'TetronIO/junctional.io' -Token 't' -TimeoutSeconds 0)

        $results.Status | Should -Be @('ProxyStale', 'ProxyStale')
    }

    It 'refuses a README with no proxied screenshots, rather than reporting success' {
        { Clear-ReadmeImageCache -Html '<p>none</p>' -SiteRepository 'TetronIO/junctional.io' -Token 't' } |
            Should -Throw '*no screenshots*'
    }
}
