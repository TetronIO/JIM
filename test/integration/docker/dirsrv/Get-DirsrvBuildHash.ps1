# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Content hash of the 389 Directory Server lab image sources.

.DESCRIPTION
    Dot-source this file and call Get-DirsrvBuildHash. Build-DirsrvImage.ps1 stamps
    the result on the image as the jim.dirsrv.build-hash label; the integration test
    runner recomputes it and rebuilds the image when the label no longer matches.

    One list, one place: every file under the fixture directory except the scripts
    that consume the hash (this file, its Pester tests and Build-DirsrvImage.ps1) is
    hashed, in ordinal order of its relative path, with the path itself folded into
    the stream. Adding, renaming or editing any fixture file changes the hash without
    anyone maintaining a list; the OpenLDAP fixture's hand-maintained pair drifted,
    which is what this avoids.

.EXAMPLE
    . ./Get-DirsrvBuildHash.ps1
    Get-DirsrvBuildHash
#>

function Get-DirsrvBuildHash {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        # The fixture directory (test/integration/docker/dirsrv). Defaults to the
        # directory this script lives in, so callers dot-sourcing it need pass nothing.
        [Parameter(Mandatory = $false)]
        [string]$FixtureDirectory = $PSScriptRoot
    )

    $excludedFileNames = @('Build-DirsrvImage.ps1', 'Get-DirsrvBuildHash.ps1', 'Get-DirsrvBuildHash.Tests.ps1')
    $root = (Resolve-Path -LiteralPath $FixtureDirectory).Path.TrimEnd([char]'\', [char]'/')

    $byRelativePath = @{}
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
        if ($excludedFileNames -contains $file.Name) { continue }
        $relative = $file.FullName.Substring($root.Length).TrimStart([char]'\', [char]'/').Replace('\', '/')
        $byRelativePath[$relative] = $file.FullName
    }

    $orderedPaths = [System.Collections.Generic.List[string]]::new([string[]]$byRelativePath.Keys)
    $orderedPaths.Sort([System.StringComparer]::Ordinal)

    $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($relative in $orderedPaths) {
            $hasher.AppendData([System.Text.Encoding]::UTF8.GetBytes("$relative`n"))
            $hasher.AppendData([System.IO.File]::ReadAllBytes($byRelativePath[$relative]))
        }
        $digest = $hasher.GetHashAndReset()
    }
    finally {
        $hasher.Dispose()
    }

    return [System.BitConverter]::ToString($digest).Replace('-', '').Substring(0, 16).ToLowerInvariant()
}
