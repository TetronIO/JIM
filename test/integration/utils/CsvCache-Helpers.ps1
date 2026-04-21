# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Helper functions for the CSV cache used by Get-OrGenerate-TestCSV.ps1.

.DESCRIPTION
    Extracted into its own file so tests (Test-CsvCache.ps1) can dot-source the helpers
    without triggering the wrapper's param-block defaults or orchestration.
#>

# CSVs that are safe to cache (byte-deterministic output).
# cross-domain-users.csv is excluded by design: it is a header-only export target that the
# File connector appends to during the test. Regenerating it fresh on every run prevents
# any risk of stale or growing cached copies leaking state across runs.
$script:CacheableCsvs = @("hr-users.csv", "departments.csv", "training-records.csv")

function Get-CsvCacheKey {
    <#
    .SYNOPSIS
        Compute the 16-hex-char content hash used to key the CSV cache archive.
    .DESCRIPTION
        Hashes (in order): whole Generate-TestCSV.ps1, whole utils/Test-Helpers.ps1, the template
        name, and the PowerShell major version. Any of these changing invalidates the cache.
        Hashing whole files (rather than extracting specific functions) matches Get-PopulateScriptHash
        in Build-SambaSnapshots.ps1; it over-invalidates on unrelated helper edits, which is a price
        worth paying for simplicity and safety.
    #>
    param(
        [Parameter(Mandatory=$true)][string]$IntegrationRoot,
        [Parameter(Mandatory=$true)][string]$Template
    )

    $filesToHash = @(
        "$IntegrationRoot/Generate-TestCSV.ps1",
        "$IntegrationRoot/utils/Test-Helpers.ps1"
    )

    $combinedContent = ""
    foreach ($file in $filesToHash) {
        if (-not (Test-Path $file)) {
            throw "Expected hash input '$file' not found."
        }
        $combinedContent += Get-Content -Path $file -Raw
    }
    $combinedContent += "template=$Template"
    $combinedContent += "psmajor=$($PSVersionTable.PSVersion.Major)"

    $hashBytes = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($combinedContent)
    )
    return [System.BitConverter]::ToString($hashBytes).Replace("-", "").Substring(0, 16).ToLower()
}

function Get-CsvCacheArchivePath {
    param(
        [Parameter(Mandatory=$true)][string]$CacheRoot,
        [Parameter(Mandatory=$true)][string]$Template,
        [Parameter(Mandatory=$true)][string]$Hash16
    )
    return Join-Path $CacheRoot "csv-$Template-$Hash16.tar"
}

function Invoke-CrossDomainTargetRegeneration {
    <#
    .SYNOPSIS
        Write a fresh header-only cross-domain-users.csv into $OutputPath.
    .DESCRIPTION
        Kept in sync with the equivalent block in Generate-TestCSV.ps1 (step 4). Must be invoked on
        every run, including cache hits, because cross-domain-users.csv is not cached.
    #>
    param(
        [Parameter(Mandatory=$true)][string]$OutputPath
    )

    $crossDomainCsvPath = Join-Path $OutputPath "cross-domain-users.csv"
    $crossDomainHeaders = @(
        "samAccountName",
        "displayName",
        "email",
        "department",
        "employeeId",
        "company",
        "pronouns"
    )
    $crossDomainHeaders -join "," | Set-Content -Path $crossDomainCsvPath -Encoding UTF8
    return $crossDomainCsvPath
}

function Invoke-ConnectorVolumeSeeding {
    <#
    .SYNOPSIS
        Stream all four CSVs into /connector-files inside jim.worker with correct ownership.
    .DESCRIPTION
        Kept in sync with the equivalent block in Generate-TestCSV.ps1 (step 5). The cache stores
        file contents only; the connector-files volume state is never cached, so this must run on
        every invocation including cache hits. Assumes Write-FileToConnectorVolume is available
        (imported from utils/Test-Helpers.ps1 by the caller).
    #>
    param(
        [Parameter(Mandatory=$true)][string]$OutputPath
    )

    Write-TestStep "Seed" "Seeding files into jim-connector-files-volume"
    Write-Host "  Streaming CSV files into jim.worker..." -ForegroundColor Gray
    Write-FileToConnectorVolume -SourcePath (Join-Path $OutputPath "hr-users.csv")           -DestinationPath "/connector-files/test-data/hr-users.csv"
    Write-FileToConnectorVolume -SourcePath (Join-Path $OutputPath "departments.csv")        -DestinationPath "/connector-files/test-data/departments.csv"
    Write-FileToConnectorVolume -SourcePath (Join-Path $OutputPath "training-records.csv")   -DestinationPath "/connector-files/test-data/training-records.csv"
    Write-FileToConnectorVolume -SourcePath (Join-Path $OutputPath "cross-domain-users.csv") -DestinationPath "/connector-files/test-data/cross-domain-users.csv"
    Write-Host "  ✓ Files seeded into /connector-files/test-data (owned by app:app)" -ForegroundColor Green
}

function Save-CsvsToCache {
    param(
        [Parameter(Mandatory=$true)][string]$ArchivePath,
        [Parameter(Mandatory=$true)][string]$OutputPath,
        [Parameter(Mandatory=$true)][string]$Template,
        [Parameter(Mandatory=$true)][string]$Hash16
    )

    $cacheDir = Split-Path -Parent $ArchivePath
    if (-not (Test-Path $cacheDir)) {
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    }

    $manifest = [PSCustomObject]@{
        template   = $Template
        hash16     = $Hash16
        psMajor    = $PSVersionTable.PSVersion.Major
        files      = $script:CacheableCsvs
        createdUtc = (Get-Date).ToUniversalTime().ToString("o")
    }
    $manifestPath = Join-Path $OutputPath "manifest.json"
    $manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8
    try {
        $filesToArchive = $script:CacheableCsvs + @("manifest.json")
        $tmpArchive = "$ArchivePath.tmp"
        & tar -cf $tmpArchive -C $OutputPath @filesToArchive
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed to create cache archive (exit code $LASTEXITCODE)"
        }
        # Atomic replace so a concurrent reader never sees a half-written tar.
        Move-Item -Path $tmpArchive -Destination $ArchivePath -Force
    }
    finally {
        if (Test-Path $manifestPath) { Remove-Item -Path $manifestPath -Force }
    }
}

function Restore-CsvsFromCache {
    param(
        [Parameter(Mandatory=$true)][string]$ArchivePath,
        [Parameter(Mandatory=$true)][string]$OutputPath
    )

    if (-not (Test-Path $OutputPath)) {
        New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    }

    & tar -xf $ArchivePath -C $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "tar failed to extract cache archive $ArchivePath (exit code $LASTEXITCODE)"
    }

    # manifest.json is an artefact of the archive only; remove from $OutputPath so the
    # output mirrors what Generate-TestCSV.ps1 would produce.
    $manifestPath = Join-Path $OutputPath "manifest.json"
    if (Test-Path $manifestPath) { Remove-Item -Path $manifestPath -Force }
}
