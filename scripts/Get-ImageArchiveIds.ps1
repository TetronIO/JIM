# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Returns the IDs of the images an OCI image archive holds.

.DESCRIPTION
    Reads an archive written by docker save from Docker's containerd image store (an OCI image layout) and returns
    the config digest of every image it can load: each platform whose manifest and config the archive carries.
    Platforms the archive only lists, and attestation manifests, are left out. Every blob read is checked against
    its digest.

    The config digest is the ID Docker's classic image store gives an image it loads. That store drops the registry
    digest an image is pinned by, so the release bundle records these IDs, and the installer checks the image it
    loaded against them before running it by its ID (see deploy/setup.sh).

.PARAMETER ArchivePath
    The image archive (.tar) to read.

.EXAMPLE
    ./scripts/Get-ImageArchiveIds.ps1 -ArchivePath ./release-output/jim-release-0.16.0/docker-images/postgres-18.tar
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'

$indexTypes = @('application/vnd.oci.image.index.v1+json', 'application/vnd.docker.distribution.manifest.list.v2+json')
$manifestTypes = @('application/vnd.oci.image.manifest.v1+json', 'application/vnd.docker.distribution.manifest.v2+json')

# The archive's members, by their name without a leading ./, each mapped to the name tar knows it by.
$names = tar -tf $ArchivePath
if ($LASTEXITCODE -ne 0) {
    throw "Could not read the archive $ArchivePath"
}
$members = @{}
foreach ($name in $names) {
    $members[$name -replace '^\./', ''] = $name
}

$workPath = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workPath | Out-Null

# A member's bytes, or $null when the archive does not carry it.
function Read-Member {
    param([string]$Name)
    if (-not $members.ContainsKey($Name)) {
        return $null
    }
    tar -xf $ArchivePath -C $workPath $members[$Name]
    if ($LASTEXITCODE -ne 0) {
        throw "Could not extract $Name from $ArchivePath"
    }
    , [IO.File]::ReadAllBytes((Join-Path $workPath $Name))
}

# A blob's bytes, checked against its digest, or $null when the archive does not carry it.
function Read-Blob {
    param([string]$Digest)
    if ($Digest -notmatch '^sha256:([0-9a-f]{64})$') {
        throw "Unsupported digest '$Digest' in $ArchivePath"
    }
    $bytes = Read-Member "blobs/sha256/$($Matches[1])"
    if ($null -eq $bytes) {
        return $null
    }
    $actual = 'sha256:' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if ($actual -ne $Digest) {
        throw "The blob $Digest in $ArchivePath does not match its digest ($actual)"
    }
    , $bytes
}

function Get-DescriptorImageIds {
    param($Descriptor)

    # Attestation manifests describe an image rather than being one; nothing loads them.
    $annotations = $Descriptor.PSObject.Properties['annotations']?.Value
    if ($annotations -and $annotations.PSObject.Properties['vnd.docker.reference.type']?.Value -eq 'attestation-manifest') {
        return
    }
    if ($Descriptor.PSObject.Properties['platform']?.Value.os -eq 'unknown') {
        return
    }

    $bytes = Read-Blob $Descriptor.digest
    if ($null -eq $bytes) {
        return
    }
    $document = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json

    if ($Descriptor.mediaType -in $indexTypes) {
        foreach ($manifest in $document.manifests) {
            Get-DescriptorImageIds $manifest
        }
    }
    elseif ($Descriptor.mediaType -in $manifestTypes) {
        if ($null -ne (Read-Blob $document.config.digest)) {
            $document.config.digest
        }
    }
}

try {
    $indexBytes = Read-Member 'index.json'
    if ($null -eq $indexBytes) {
        throw "$ArchivePath has no index.json, so it is not an OCI image layout. Save the image with Docker's containerd image store."
    }
    $index = [Text.Encoding]::UTF8.GetString($indexBytes) | ConvertFrom-Json

    $ids = @(foreach ($descriptor in $index.manifests) { Get-DescriptorImageIds $descriptor }) | Sort-Object -Unique
    if ($ids.Count -eq 0) {
        throw "$ArchivePath holds no image it could load: no platform's manifest and config are in it"
    }
    $ids
}
finally {
    Remove-Item -Recurse -Force $workPath -ErrorAction SilentlyContinue
}
