# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Cache-aware wrapper around Generate-TestCSV.ps1.

.DESCRIPTION
    Test CSV generation is fully deterministic (see Generate-TestCSV.ps1 and the TrainingEpoch /
    account-expiry comments in utils/Test-Helpers.ps1), so the output for a given template + generator
    version is reproducible to the byte. This wrapper caches the three large, slow-to-generate CSVs
    (hr-users, departments, training-records) as a tar archive keyed by a content hash, and restores
    them in place of regenerating on subsequent runs.

    Behaviour:
      - Cache hit:  extract cached tar into -OutputPath, regenerate cross-domain-users.csv fresh,
                    seed the connector-files volume, done.
      - Cache miss: invoke Generate-TestCSV.ps1 with -SkipSeed to produce files without touching the
                    connector volume, archive the three cacheable CSVs into the cache, then
                    regenerate cross-domain-users.csv and seed the connector-files volume.

    cross-domain-users.csv is intentionally *not* cached: it is a header-only export target that the
    File connector appends to during the test. Regenerating it fresh on every run prevents any risk
    of stale / growing cached copies leaking state across runs.

.PARAMETER Template
    Data scale template (forwarded to Generate-TestCSV.ps1).

.PARAMETER OutputPath
    Path where CSV files should be placed (default: ./test-data; forwarded to Generate-TestCSV.ps1).

.PARAMETER CachePath
    Override the cache directory. Default: <OutputPath>/.cache.

.PARAMETER IgnoreCache
    Force regeneration and overwrite any existing cache entry.

.PARAMETER NoCache
    Generate as normal but do not read from or write to the cache.

.EXAMPLE
    ./Get-OrGenerate-TestCSV.ps1 -Template Scale100K

.EXAMPLE
    ./Get-OrGenerate-TestCSV.ps1 -Template Small -IgnoreCache
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100K", "Scale200K", "Scale500K", "Scale750K", "Scale1M")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$OutputPath = "./test-data",

    [Parameter(Mandatory=$false)]
    [string]$CachePath,

    [Parameter(Mandatory=$false)]
    [switch]$IgnoreCache,

    [Parameter(Mandatory=$false)]
    [switch]$NoCache
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = $PSScriptRoot
. "$scriptRoot/utils/Test-Helpers.ps1"
. "$scriptRoot/utils/CsvCache-Helpers.ps1"

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}
$resolvedOutputPath = (Resolve-Path $OutputPath).Path

if (-not $CachePath) {
    $CachePath = Join-Path $resolvedOutputPath ".cache"
}

$hash16 = Get-CsvCacheKey -IntegrationRoot $scriptRoot -Template $Template
$archivePath = Get-CsvCacheArchivePath -CacheRoot $CachePath -Template $Template -Hash16 $hash16

Write-TestSection "CSV cache lookup ($Template)"
Write-Host "Cache key:   $hash16" -ForegroundColor Gray
Write-Host "Cache file:  $archivePath" -ForegroundColor Gray

$cacheHit = (-not $IgnoreCache) -and (-not $NoCache) -and (Test-Path $archivePath)

if ($cacheHit) {
    Write-Host "Cache hit; restoring CSVs..." -ForegroundColor Green
    $restoreStart = Get-Date
    Restore-CsvsFromCache -ArchivePath $archivePath -OutputPath $resolvedOutputPath
    $restoreSeconds = [math]::Round(((Get-Date) - $restoreStart).TotalSeconds, 2)
    Write-Host "  ✓ Restored cached CSVs in ${restoreSeconds}s" -ForegroundColor Green
}
else {
    if ($IgnoreCache) {
        Write-Host "Cache bypassed (-IgnoreCache)." -ForegroundColor Yellow
    }
    elseif ($NoCache) {
        Write-Host "Cache disabled (-NoCache)." -ForegroundColor Yellow
    }
    else {
        Write-Host "Cache miss; generating..." -ForegroundColor Yellow
    }

    & "$scriptRoot/Generate-TestCSV.ps1" -Template $Template -OutputPath $resolvedOutputPath -SkipSeed
    # Generate-TestCSV.ps1 uses $ErrorActionPreference = "Stop"; a failure throws rather than
    # setting $LASTEXITCODE, so we don't check it here.

    if (-not $NoCache) {
        Write-Host "Archiving CSVs into cache..." -ForegroundColor Gray
        Save-CsvsToCache -ArchivePath $archivePath -OutputPath $resolvedOutputPath -Template $Template -Hash16 $hash16
        Write-Host "  ✓ Wrote $archivePath" -ForegroundColor Green
    }
}

# Always regenerate cross-domain-users.csv fresh (not cached).
$crossDomainPath = Invoke-CrossDomainTargetRegeneration -OutputPath $resolvedOutputPath
Write-Host "  ✓ Wrote fresh $crossDomainPath (empty export target)" -ForegroundColor Green

# Always seed the connector-files volume (not cached).
Invoke-ConnectorVolumeSeeding -OutputPath $resolvedOutputPath
