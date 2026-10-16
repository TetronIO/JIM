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
Write-Host "`nChecking every link in the built site with lychee (offline)..."
& docker run --rm --init -v "${siteFullPath}:/site:ro" $LycheeImage --offline --no-progress --root-dir /site '/site/**/*.html'
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nDocs link check FAILED: each error above names the built page and the link that does not resolve. Fix the link in the page's Markdown source." -ForegroundColor Red
    exit 1
}

Write-Host "`nDocs site check passed: the site builds strictly and every link in it resolves." -ForegroundColor Green
exit 0
