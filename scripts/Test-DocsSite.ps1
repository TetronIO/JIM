# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds the documentation site strictly, then checks every link in the built
    site, failing on a broken link, anchor or page.

.DESCRIPTION
    The two link checks of the documentation standard (rules LNK-1 and LNK-2,
    #1454). Until they ran at pull request time, nothing checked the docs before
    merge: `mkdocs build --strict` ran only after a push to main, and MkDocs
    reports a broken anchor at INFO by default, so a link to a heading that did
    not exist rendered as a working link that went nowhere.

      1. `mkdocs build --strict`, with the `validation:` settings in mkdocs.yml,
         fails on a link to a missing page or heading, an absolute link, and a
         page that exists but is missing from the nav.
      2. lychee, run offline over the built HTML, checks every link the build
         left for the browser to resolve, which MkDocs cannot see: links into
         the Scalar API reference and links written in raw HTML. One such link
         (`../api/reference/` on the PowerShell System page) had been a 404 on
         the live site.
      3. No emoji in the built site (STY-1), outside code. Test-DocsConformance.ps1
         checks each page's Markdown; this catches what reaches a page any other
         way, such as the changelog the build hook inserts or text inside an
         inlined diagram.

    Both read the docs as Markdown sources and built HTML only, so they keep
    working whichever generator builds the site (#2058).

    lychee runs from its official container image, pinned by digest (an
    approved dependency; see engineering/DEPENDENCY_PINNING.md). It needs only
    read access to the built site and never touches the network: --offline
    checks local files and skips every external URL.

    Requires `mkdocs` (with mkdocs-material and mkdocs-glightbox) on PATH and a
    running Docker daemon. Run from the repository root.

.PARAMETER SiteDir
    Where to build the site. Defaults to a new directory under the system temp
    path, so a local run never touches the repository's own site/ folder.

.EXAMPLE
    pwsh -File ./scripts/Test-DocsSite.ps1
    Builds the site strictly and checks every link in it.
#>
[CmdletBinding()]
param(
    [string]$SiteDir = (Join-Path ([System.IO.Path]::GetTempPath()) "jim-docs-site-$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'

# lychee 0.24.2, the official multi-architecture image, pinned by digest.
# Docker verifies the digest; the tag is kept beside it for readability. tooling-pin-check
# watches the pin and proposes the next release's tag and digest together.
$LycheeImage = 'lycheeverse/lychee:0.24.2@sha256:e2d19e57cf6ab037026f20b8e449a1f30d9d7f81eef4194763aab2eab20bd28d'

if (-not (Test-Path -LiteralPath 'mkdocs.yml')) {
    Write-Error "mkdocs.yml not found; run this script from the repository root."
    exit 2
}
foreach ($tool in 'mkdocs', 'docker') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Error "'$tool' is not on PATH; it is required to build and check the docs site."
        exit 2
    }
}

Write-Host "Building the docs site strictly into '$SiteDir'..."
& mkdocs build --strict --site-dir $SiteDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nDocs site build FAILED: fix the warnings above (a broken link or anchor, an absolute link, or a page missing from the nav)." -ForegroundColor Red
    exit 1
}

$siteFullPath = (Resolve-Path -LiteralPath $SiteDir).Path

# STY-1 over the built pages. The pattern matches Test-DocsConformance.ps1's $script:EmojiRegex
# (described there); keep the two in step. Code is exempt, so <pre> and <code> are dropped first.
Write-Host "`nChecking the built site for emoji (STY-1)..."
$emoji = [regex]::new('(?:(?:[\u2600-\u27BF\u2B05-\u2B07\u2B1B\u2B1C\u2B50\u2B55\u231A\u231B\u23E9-\u23FA\u2139\u24C2\u25AA\u25AB\u25B6\u25C0\u25FB-\u25FE\u21A9\u21AA\u2934\u2935\u3030\u303D\u3297\u3299\u203C\u2049]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDEFF])\uFE0F?|[^\s\uFE0F]\uFE0F)')
$strip = [regex]::new('<(script|style|pre|code)\b.*?</\1>', [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$emojiPages = 0
foreach ($file in Get-ChildItem -LiteralPath $siteFullPath -Recurse -Filter '*.html') {
    $text = [System.Net.WebUtility]::HtmlDecode(($strip.Replace((Get-Content -LiteralPath $file.FullName -Raw), ' ') -replace '<[^>]+>', ' '))
    $found = @($emoji.Matches($text) | ForEach-Object Value | Select-Object -Unique)
    if ($found.Count) {
        $emojiPages++
        Write-Host ("  {0}: {1}" -f [System.IO.Path]::GetRelativePath($siteFullPath, $file.FullName), ($found -join ' '))
    }
}
if ($emojiPages) {
    Write-Host "`nDocs emoji check FAILED: $emojiPages built page(s) carry emoji (STY-1). Use a Material Design icon in the page's source, or, for text a build hook inserts, strip it in the hook." -ForegroundColor Red
    exit 1
}
Write-Host 'No emoji in the built site.'
Write-Host "`nChecking every link in the built site with lychee (offline)..."
& docker run --rm --init -v "${siteFullPath}:/site:ro" $LycheeImage --offline --no-progress --root-dir /site '/site/**/*.html'
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nDocs link check FAILED: each error above names the built page and the link that does not resolve. Fix the link in the page's Markdown source." -ForegroundColor Red
    exit 1
}

Write-Host "`nDocs site check passed: the site builds strictly and every link in it resolves." -ForegroundColor Green
exit 0
