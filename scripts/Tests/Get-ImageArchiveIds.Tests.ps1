# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Get-ImageArchiveIds.ps1.

.DESCRIPTION
    Builds small OCI image archives, shaped as docker save writes them from Docker's containerd image store, and
    checks the IDs read from them: the config digest of each image the archive can load, which is the ID Docker's
    classic image store gives the image once loaded. Platforms whose content the archive lacks, and attestation
    manifests, are not images anyone loads, so they have no ID to record.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Get-ImageArchiveIds.ps1')).Path

    $script:IndexType = 'application/vnd.oci.image.index.v1+json'
    $script:ManifestType = 'application/vnd.oci.image.manifest.v1+json'
    $script:ConfigType = 'application/vnd.oci.image.config.v1+json'

    function Get-Sha256 {
        param([string]$Content)
        $bytes = [Text.Encoding]::UTF8.GetBytes($Content)
        'sha256:' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }

    # An archive under construction: a folder holding an OCI layout.
    function New-Layout {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $path 'blobs/sha256') -Force | Out-Null
        '{"imageLayoutVersion":"1.0.0"}' | Set-Content -NoNewline (Join-Path $path 'oci-layout')
        $path
    }

    # Adds a blob, unless -Missing, and returns its descriptor's digest either way.
    function Add-Blob {
        param([string]$Layout, [string]$Content, [switch]$Missing)
        $digest = Get-Sha256 $Content
        if (-not $Missing) {
            $Content | Set-Content -NoNewline (Join-Path $Layout "blobs/sha256/$($digest.Substring(7))")
        }
        $digest
    }

    function Add-Image {
        param([string]$Layout, [string]$Architecture, [switch]$Missing)
        $config = Add-Blob $Layout "{`"architecture`":`"$Architecture`",`"os`":`"linux`",`"rootfs`":{`"type`":`"layers`",`"diff_ids`":[]}}"
        $manifest = @{
            schemaVersion = 2
            mediaType     = $script:ManifestType
            config        = @{ mediaType = $script:ConfigType; digest = $config; size = 1 }
            layers        = @()
        } | ConvertTo-Json -Depth 5 -Compress
        [pscustomobject]@{
            Config   = $config
            Manifest = Add-Blob $Layout $manifest -Missing:$Missing
        }
    }

    function Write-Index {
        param([string]$Path, [object[]]$Manifests)
        @{ schemaVersion = 2; mediaType = $script:IndexType; manifests = $Manifests } |
            ConvertTo-Json -Depth 6 -Compress | Set-Content -NoNewline $Path
    }

    function New-Archive {
        param([string]$Layout)
        $archive = "$Layout.tar"
        tar -cf $archive -C $Layout .
        $archive
    }
}

Describe 'Get-ImageArchiveIds' {
    It 'returns the config digest of the platform image a multi-platform archive carries' {
        $layout = New-Layout
        $amd64 = Add-Image $layout 'amd64'
        $arm64 = Add-Image $layout 'arm64' -Missing
        $attestation = Add-Image $layout 'unknown'
        $imageIndex = @{
            schemaVersion = 2
            mediaType     = $script:IndexType
            manifests     = @(
                @{ mediaType = $script:ManifestType; digest = $amd64.Manifest; size = 1; platform = @{ architecture = 'amd64'; os = 'linux' } }
                @{ mediaType = $script:ManifestType; digest = $arm64.Manifest; size = 1; platform = @{ architecture = 'arm64'; os = 'linux' } }
                @{
                    mediaType   = $script:ManifestType; digest = $attestation.Manifest; size = 1
                    platform    = @{ architecture = 'unknown'; os = 'unknown' }
                    annotations = @{ 'vnd.docker.reference.type' = 'attestation-manifest'; 'vnd.docker.reference.digest' = $amd64.Manifest }
                }
            )
        } | ConvertTo-Json -Depth 6 -Compress
        $indexDigest = Add-Blob $layout $imageIndex
        Write-Index (Join-Path $layout 'index.json') @(
            @{ mediaType = $script:IndexType; digest = $indexDigest; size = 1; annotations = @{ 'io.containerd.image.name' = 'docker.io/library/postgres:18.6' } }
        )

        & $script:ScriptPath -ArchivePath (New-Archive $layout) | Should -Be @($amd64.Config)
    }

    It 'returns the config digest of a single-platform archive' {
        $layout = New-Layout
        $image = Add-Image $layout 'amd64'
        Write-Index (Join-Path $layout 'index.json') @(@{ mediaType = $script:ManifestType; digest = $image.Manifest; size = 1 })

        & $script:ScriptPath -ArchivePath (New-Archive $layout) | Should -Be @($image.Config)
    }

    It 'refuses an archive with no image it could load' {
        $layout = New-Layout
        $image = Add-Image $layout 'amd64' -Missing
        Write-Index (Join-Path $layout 'index.json') @(@{ mediaType = $script:ManifestType; digest = $image.Manifest; size = 1 })

        { & $script:ScriptPath -ArchivePath (New-Archive $layout) } | Should -Throw '*no image*'
    }

    It 'refuses an archive whose content does not match its digest' {
        $layout = New-Layout
        $image = Add-Image $layout 'amd64'
        'tampered' | Set-Content -NoNewline (Join-Path $layout "blobs/sha256/$($image.Manifest.Substring(7))")
        Write-Index (Join-Path $layout 'index.json') @(@{ mediaType = $script:ManifestType; digest = $image.Manifest; size = 1 })

        { & $script:ScriptPath -ArchivePath (New-Archive $layout) } | Should -Throw '*does not match*'
    }

    It 'refuses an archive that is not an OCI image layout' {
        $layout = New-Layout
        Remove-Item (Join-Path $layout 'oci-layout')
        'hello' | Set-Content (Join-Path $layout 'readme.txt')

        { & $script:ScriptPath -ArchivePath (New-Archive $layout) } | Should -Throw '*index.json*'
    }
}
