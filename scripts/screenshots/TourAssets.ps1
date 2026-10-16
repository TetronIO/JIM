# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refreshes junctional.io's product tour images from a captured screenshot library (#582).

.DESCRIPTION
    The tour's steps name their images in the site's two dictionaries (src/i18n/en-GB.ts and en-US.ts), each step an
    `image: { dark: "...", light: "..." }` with an optional `version` pinning it to an older folder, and the dictionary
    a tour-wide `imageVersion` naming the folder under src/assets/tour/ the rest come from. A refresh copies the images
    the steps show into a new version folder, moves both dictionaries to it, and removes folders no step uses any more.

    Only the images the tour shows are published, not the whole library a capture produces: every image committed to
    the site stays in its repository's history for good, and most of the library is not on any page.

    The homepage images are also written to public/assets/screenshots/, which the site serves at a fixed address
    (https://junctional.io/assets/screenshots/homepage-dark.png). The tour's own copies are served under a name that
    changes with their content, so a link from outside the site (JIM's README) would break at the next refresh.

    The dictionaries are a spelling fork of each other, so their image names and versions must be identical; a refresh
    refuses dictionaries that disagree rather than publish a tour that breaks in one locale.
#>

Set-StrictMode -Version Latest

$script:TourImageVersionPattern = '\bimageVersion:\s*"([^"]*)"'
$script:TourImageBlockPattern = 'image:\s*\{([^{}]*)\}'
$script:TourReleaseVersionPattern = '^v\d+\.\d+(\.\d+)?(-[0-9A-Za-z.]+)?$'

# The tour-wide image version: the folder under src/assets/tour/ the steps' images come from, unless pinned.
function Get-TourImageVersion {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    $found = [regex]::Matches($Text, $script:TourImageVersionPattern)
    if ($found.Count -ne 1) {
        throw "Expected exactly one imageVersion in the dictionary, found $($found.Count)."
    }
    return $found[0].Groups[1].Value
}

# The dictionary with its tour-wide image version changed, and nothing else.
function Set-TourImageVersion {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Version
    )

    if ($Version -notmatch $script:TourReleaseVersionPattern) {
        throw "'$Version' is not a release version folder name (v1.2.3)."
    }
    Get-TourImageVersion -Text $Text | Out-Null
    return [regex]::Replace($Text, $script:TourImageVersionPattern, "imageVersion: ""$Version""")
}

# Every image the tour's steps show: its file name, and the version a step pins it to ($null for the tour-wide one).
function Get-TourImageReference {
    param([Parameter(Mandatory)][string]$Text)

    foreach ($block in [regex]::Matches($Text, $script:TourImageBlockPattern)) {
        $body = $block.Groups[1].Value
        $pinned = [regex]::Match($body, '\bversion:\s*"([^"]+)"')
        $version = if ($pinned.Success) { $pinned.Groups[1].Value } else { $null }
        foreach ($image in [regex]::Matches($body, '\b(?:dark|light):\s*"([^"]+)"')) {
            [pscustomobject]@{ FileName = $image.Groups[1].Value; Version = $version }
        }
    }
}

<#
.SYNOPSIS
    Publishes a screenshot library's tour images into a junctional.io checkout as a new version.
.DESCRIPTION
    Checks everything before changing anything: both dictionaries show the same images, and every image the tour
    takes from the tour-wide version is in the library. Then writes src/assets/tour/<Version>/ afresh, moves both
    dictionaries to it, and removes the version folders no step uses. Run again for the same version, it replaces the
    folder rather than adding to it.
.OUTPUTS
    The version, the version it replaced, the images published, those also written to the fixed address, and the
    folders removed.
#>
function Update-TourAssetFolder {
    param(
        [Parameter(Mandatory)][string]$SitePath,
        [Parameter(Mandatory)][string]$ScreenshotPath,
        [Parameter(Mandatory)][string]$Version,
        [string[]]$StableImages = @('homepage-dark.png', 'homepage-light.png')
    )

    if ($Version -notmatch $script:TourReleaseVersionPattern) {
        throw "'$Version' is not a release version folder name (v1.2.3)."
    }

    $dictionaries = [ordered]@{}
    foreach ($locale in @('en-GB', 'en-US')) {
        $path = Join-Path $SitePath 'src' 'i18n' "$locale.ts"
        if (-not (Test-Path -LiteralPath $path)) { throw "No $locale dictionary at $path; is $SitePath a junctional.io checkout?" }
        $dictionaries[$locale] = [pscustomobject]@{ Path = $path; Text = [System.IO.File]::ReadAllText($path) }
    }

    $describe = { param($refs) @($refs | ForEach-Object { "$($_.FileName)@$($_.Version)" } | Sort-Object -Unique) }
    $gbReferences = @(Get-TourImageReference -Text $dictionaries['en-GB'].Text)
    $usReferences = @(Get-TourImageReference -Text $dictionaries['en-US'].Text)
    if ($gbReferences.Count -eq 0) { throw "The en-GB dictionary names no tour images; nothing to publish." }
    $difference = @(Compare-Object (& $describe $gbReferences) (& $describe $usReferences))
    if ($difference.Count -gt 0) {
        $items = ($difference | ForEach-Object { "$($_.InputObject) (only in $(if ($_.SideIndicator -eq '<=') { 'en-GB' } else { 'en-US' }))" }) -join ', '
        throw "The en-GB and en-US dictionaries show different tour images: $items. Their image names must be identical."
    }

    $previousVersion = Get-TourImageVersion -Text $dictionaries['en-GB'].Text
    $usVersion = Get-TourImageVersion -Text $dictionaries['en-US'].Text
    if ($usVersion -ne $previousVersion) {
        throw "The dictionaries disagree on imageVersion (en-GB $previousVersion, en-US $usVersion)."
    }

    $needed = @($gbReferences | Where-Object { -not $_.Version } | ForEach-Object FileName | Sort-Object -Unique)
    $missing = @(@($needed) + @($StableImages) | Sort-Object -Unique | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ScreenshotPath $_) -PathType Leaf) })
    if ($missing.Count -gt 0) {
        throw "The screenshot library at $ScreenshotPath is missing image(s) the tour shows: $($missing -join ', ')."
    }

    # Everything checked; now change the site.
    $tourRoot = Join-Path $SitePath 'src' 'assets' 'tour'
    $target = Join-Path $tourRoot $Version
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($name in $needed) {
        Copy-Item -LiteralPath (Join-Path $ScreenshotPath $name) -Destination (Join-Path $target $name)
    }

    $stableRoot = Join-Path $SitePath 'public' 'assets' 'screenshots'
    New-Item -ItemType Directory -Path $stableRoot -Force | Out-Null
    foreach ($name in $StableImages) {
        Copy-Item -LiteralPath (Join-Path $ScreenshotPath $name) -Destination (Join-Path $stableRoot $name) -Force
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    foreach ($dictionary in $dictionaries.Values) {
        [System.IO.File]::WriteAllText($dictionary.Path, (Set-TourImageVersion -Text $dictionary.Text -Version $Version), $utf8NoBom)
    }

    $keep = @(@($Version) + @($gbReferences | Where-Object { $_.Version } | ForEach-Object Version) | Sort-Object -Unique)
    $removed = @(Get-ChildItem -LiteralPath $tourRoot -Directory | Where-Object { $_.Name -notin $keep })
    foreach ($folder in $removed) { Remove-Item -LiteralPath $folder.FullName -Recurse -Force }

    return [pscustomobject]@{
        Version         = $Version
        PreviousVersion = $previousVersion
        Published       = $needed
        Stable          = @($StableImages)
        RemovedFolders  = @($removed | ForEach-Object Name)
    }
}

<#
.SYNOPSIS
    Builds a junctional.io checkout as its deploy workflow does, failing when the site does not build.
.DESCRIPTION
    A release publishes the refresh with no one looking first, so the site must build with the new images (Astro's
    type check, then every page and image variant) before anything is committed. The output goes to dist/ and
    node_modules/, which the site's .gitignore keeps out of the commit.
#>
function Invoke-TourSiteBuild {
    param([Parameter(Mandatory)][string]$SitePath)

    Push-Location -LiteralPath $SitePath
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed in $SitePath (exit $LASTEXITCODE); the site was not built." }
        npm run build
        if ($LASTEXITCODE -ne 0) {
            throw "The site does not build with these screenshots (npm run build exited $LASTEXITCODE); nothing was published."
        }
    }
    finally {
        Pop-Location
    }
}
