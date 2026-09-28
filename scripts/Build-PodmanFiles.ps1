# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Renders JIM's Podman deployment files for a release.

.DESCRIPTION
    Copies deploy/podman to the output folder, filling in the image references its pod files leave as
    placeholders: the registry and version of JIM's images, and the PostgreSQL image. The PostgreSQL image
    is read from docker-compose.yml, which stays its single source of truth (Dependabot maintains its digest
    there), so the two runtimes always run the same database image.

    Used by Build-ReleaseBundle.ps1 for the release bundle's podman folder, and by the release workflow for
    the files it publishes.

.PARAMETER Version
    The JIM version the pod file runs, e.g. 0.16.0.

.PARAMETER Registry
    The registry prefix of JIM's images. Defaults to ghcr.io/tetronio/.

.PARAMETER OutputPath
    The folder to write the rendered files to. Created if missing.

.EXAMPLE
    ./scripts/Build-PodmanFiles.ps1 -Version 0.16.0 -OutputPath ./release-output/podman
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter()]
    [string]$Registry = 'ghcr.io/tetronio/',

    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repoRoot 'deploy/podman'

# The version becomes part of an image reference, so only a tag Docker and Podman accept is allowed.
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
    throw "'$Version' is not a release version"
}
if ($Registry -and -not $Registry.EndsWith('/')) {
    $Registry = "$Registry/"
}

$compose = Get-Content (Join-Path $repoRoot 'docker-compose.yml') -Raw
$postgresMatch = [regex]::Match($compose, 'image:\s+(docker\.io/library/postgres:\S+)')
if (-not $postgresMatch.Success) {
    throw 'Could not find the PostgreSQL image reference in docker-compose.yml'
}
$postgresImage = $postgresMatch.Groups[1].Value

$placeholders = [ordered]@{
    '__JIM_REGISTRY__'   = $Registry
    '__JIM_VERSION__'    = $Version
    '__POSTGRES_IMAGE__' = $postgresImage
}

foreach ($file in Get-ChildItem $sourcePath -Recurse -File) {
    $relativePath = [IO.Path]::GetRelativePath($sourcePath, $file.FullName)
    $destination = Join-Path $OutputPath $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null

    $content = [IO.File]::ReadAllText($file.FullName)
    foreach ($placeholder in $placeholders.Keys) {
        $content = $content.Replace($placeholder, $placeholders[$placeholder])
    }

    $leftover = [regex]::Match($content, '__[A-Z_]+__')
    if ($leftover.Success) {
        throw "$relativePath has a placeholder this script does not fill in: $($leftover.Value)"
    }

    # LF line endings: these files are read by Podman and systemd on Linux hosts, whatever this checkout uses.
    [IO.File]::WriteAllText($destination, ($content -replace "`r`n", "`n"))
}

Write-Host "Rendered the Podman files for JIM $Version ($($Registry)jim-*:$Version, $postgresImage) in $OutputPath"
