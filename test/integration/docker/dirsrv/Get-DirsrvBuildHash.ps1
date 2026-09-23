# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The 389 Directory Server lab's hash and tag contract: base image hash, snapshot
    hash, snapshot tag and the snapshot currency check.

.DESCRIPTION
    Dot-source this file. It is the one place that defines what the 389 fixture's
    images are labelled with and how those labels are compared, shared by
    Build-DirsrvImage.ps1, Build-DirsrvSnapshots.ps1 and the integration test runner,
    so none of them can drift from the others.

    Get-DirsrvBuildHash: Build-DirsrvImage.ps1 stamps the result on the image as the
    jim.dirsrv.build-hash label (and bakes it into /data/.jim-provisioned-id, the
    instance's provenance stamp); the runner recomputes it and rebuilds the image
    when the label no longer matches. One list, one place: every file under the
    fixture directory except the scripts that consume the hash (this file, its
    Pester tests and Build-DirsrvImage.ps1) is hashed, in ordinal order of its
    relative path, with the path itself folded into the stream. Adding, renaming or
    editing any fixture file changes the hash without anyone maintaining a list; the
    OpenLDAP fixture's hand-maintained pair drifted, which is what this avoids.

    Get-DirsrvSnapshotHash: the same digest over the files whose content a populated
    snapshot depends on (the shared populate helpers, the snapshot builder and the
    scenario's own populate script). Build-DirsrvSnapshots.ps1 stamps it on the
    snapshot as jim.dirsrv.snapshot-hash.

    Get-DirsrvSnapshotImageTag: the snapshot tag shape, jim-dirsrv:<role>-<template>.

    Test-DirsrvSnapshotCurrent: whether a snapshot image can be used as it is, which
    needs both its snapshot hash and the base-image hash it was baked from to match.

.EXAMPLE
    . ./Get-DirsrvBuildHash.ps1
    Get-DirsrvBuildHash

.EXAMPLE
    . ./Get-DirsrvBuildHash.ps1
    $tag = Get-DirsrvSnapshotImageTag -Role general -Template Small
    Test-DirsrvSnapshotCurrent -ImageTag $tag -ExpectedSnapshotHash (Get-DirsrvSnapshotHash -Scenario General) -ExpectedBaseHash (Get-DirsrvBuildHash)
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

function Get-DirsrvSnapshotHash {
    <#
    .SYNOPSIS
        Content hash of the files a populated 389 snapshot depends on, for one scenario.
    .DESCRIPTION
        SHA-256 (first 16 hex characters, lower case) over the shared populate helpers, the
        snapshot builder and the scenario's populate script, in that order, with each file's
        relative path folded into the stream exactly as Get-DirsrvBuildHash does. A file that
        does not exist is skipped, so the hash can be computed in a checkout that lacks one.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('General', 'Scenario8')]
        [string]$Scenario,

        # The test/integration directory. Defaults to two levels above this file's
        # directory (test/integration/docker/dirsrv), so callers dot-sourcing it need pass nothing.
        [Parameter(Mandatory = $false)]
        [string]$IntegrationRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    )

    $populateScript = switch ($Scenario) {
        'General'   { 'Populate-OpenLDAP.ps1' }
        'Scenario8' { 'Populate-OpenLDAP-Scenario8.ps1' }
    }
    $relativePaths = @(
        'utils/Test-Helpers.ps1',
        'utils/Test-GroupHelpers.ps1',
        'Build-DirsrvSnapshots.ps1',
        $populateScript
    )
    $root = (Resolve-Path -LiteralPath $IntegrationRoot).Path.TrimEnd([char]'\', [char]'/')

    $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($relative in $relativePaths) {
            $fullPath = Join-Path $root $relative
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
            $hasher.AppendData([System.Text.Encoding]::UTF8.GetBytes("$relative`n"))
            $hasher.AppendData([System.IO.File]::ReadAllBytes($fullPath))
        }
        $digest = $hasher.GetHashAndReset()
    }
    finally {
        $hasher.Dispose()
    }

    return [System.BitConverter]::ToString($digest).Replace('-', '').Substring(0, 16).ToLowerInvariant()
}

function Get-DirsrvSnapshotImageTag {
    <#
    .SYNOPSIS
        The tag of a 389 snapshot image: jim-dirsrv:<role>-<template in lower case>.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        # general: both suffixes populated (the directory-agnostic scenarios).
        # s8: Scenario 8's shape (Source populated, Target OUs only).
        [Parameter(Mandatory = $true)]
        [ValidateSet('general', 's8')]
        [string]$Role,

        [Parameter(Mandatory = $true)]
        [string]$Template
    )

    return "jim-dirsrv:${Role}-$($Template.ToLowerInvariant())"
}

function Test-DirsrvSnapshotCurrent {
    <#
    .SYNOPSIS
        Whether a 389 snapshot image exists and was baked from the current inputs.
    .DESCRIPTION
        True only when the image exists, its jim.dirsrv.snapshot-hash label equals the
        expected snapshot hash and its jim.dirsrv.base-hash label equals the expected base
        hash. A snapshot captures the base image's configured instance (schema, suffixes,
        ACIs, plug-ins), so a snapshot baked from an older base is stale even when the
        populate scripts have not changed. A missing label counts as a mismatch. Every
        rejection is reported on the host with its reason.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ImageTag,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedSnapshotHash,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedBaseHash
    )

    # docker is invoked as a plain command (never through Get-Command) so a test can
    # replace it with a function and Pester can mock it.
    $snapshotHash = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.dirsrv.snapshot-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Snapshot '$ImageTag' not found" -ForegroundColor Yellow
        return $false
    }
    if (-not (Test-DirsrvLabelEquals -Actual $snapshotHash -Expected $ExpectedSnapshotHash)) {
        Write-Host "  Snapshot '$ImageTag' is stale: snapshot hash '$("$snapshotHash".Trim())' != $ExpectedSnapshotHash" -ForegroundColor Yellow
        return $false
    }

    $baseHash = docker image inspect $ImageTag --format '{{index .Config.Labels "jim.dirsrv.base-hash"}}' 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Snapshot '$ImageTag' could not be inspected for its base hash" -ForegroundColor Yellow
        return $false
    }
    if (-not (Test-DirsrvLabelEquals -Actual $baseHash -Expected $ExpectedBaseHash)) {
        Write-Host "  Snapshot '$ImageTag' is stale: baked from base '$("$baseHash".Trim())', current base is $ExpectedBaseHash" -ForegroundColor Yellow
        return $false
    }
    return $true
}

function Test-DirsrvLabelEquals {
    # docker image inspect prints "<no value>" for a label the image does not carry;
    # that, an empty value and a different value are all "not current".
    param($Actual, [string]$Expected)
    $value = "$Actual".Trim()
    if ([string]::IsNullOrEmpty($value) -or $value -eq '<no value>') { return $false }
    return $value -eq $Expected
}

