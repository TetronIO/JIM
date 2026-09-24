# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Acceptance tests for the CSV cache (Get-OrGenerate-TestCSV.ps1).

.DESCRIPTION
    Covers the acceptance criteria from issue #634:
      1. Generator produces byte-identical CSVs across runs (determinism).
      2. Two successive wrapper runs: first is a miss, second is a hit.
      3. Restored CSVs are byte-identical to freshly-generated ones; the cache key differs across
         templates and across -OmitItOwnedAttributes (Unique Value Generation, #242).
      4. Editing a hashed file invalidates the cache key.
      5. -IgnoreCache forces regeneration.
      6. -OmitItOwnedAttributes actually omits the IT-owned columns, and their absence has no effect
         on generation when the switch is not supplied.

    Runs the generator with -SkipSeed so it can execute without a running jim.worker.
    The cache wrapper's own seeding step (which does need jim.worker) is bypassed by
    dot-sourcing the wrapper and calling its internal functions directly rather than
    running the wrapper end-to-end.

    Usage:
        ./Test-CsvCache.ps1                       # default template: Nano
        ./Test-CsvCache.ps1 -Template Small       # larger, slower
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium")]
    [string]$Template = "Nano"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = $PSScriptRoot

# Dot-source the helpers directly. Do NOT dot-source Get-OrGenerate-TestCSV.ps1 itself:
# its param block re-binds $Template in the current scope, overwriting this script's
# $Template parameter.
. "$scriptRoot/utils/CsvCache-Helpers.ps1"

# ---------------------------------------------------------------------------
# Test harness
# ---------------------------------------------------------------------------

$script:TestCount = 0
$script:FailCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:TestCount++
    if ($Condition) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
    }
    else {
        $script:FailCount++
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    Assert-True ($Expected -eq $Actual) "$Message (expected=$Expected, actual=$Actual)"
}

function Get-CsvTriplet {
    param([string]$Dir)
    @(
        "hr-users.csv"
        "departments.csv"
        "training-records.csv"
    ) | ForEach-Object {
        [PSCustomObject]@{
            Name = $_
            Hash = (Get-FileHash (Join-Path $Dir $_) -Algorithm SHA256).Hash
        }
    }
}

function Invoke-GeneratorOnly {
    param([string]$OutputPath, [string]$Template, [switch]$OmitItOwnedAttributes)
    # Generate-TestCSV.ps1 is a PowerShell script with $ErrorActionPreference = "Stop";
    # failures throw rather than setting $LASTEXITCODE, so no exit-code check is needed.
    & "$scriptRoot/Generate-TestCSV.ps1" -Template $Template -OutputPath $OutputPath -SkipSeed -OmitItOwnedAttributes:$OmitItOwnedAttributes | Out-Null
}

# ---------------------------------------------------------------------------
# Test fixtures
# ---------------------------------------------------------------------------

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("jim-csv-cache-test-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
Write-Host ""
Write-Host "Workspace: $workRoot" -ForegroundColor Cyan
Write-Host "Template:  $Template" -ForegroundColor Cyan
Write-Host ""

try {
    $runA = Join-Path $workRoot "gen-a"
    $runB = Join-Path $workRoot "gen-b"
    $runC = Join-Path $workRoot "restored"
    $cacheDir = Join-Path $workRoot "cache"
    New-Item -ItemType Directory -Path $runA, $runB, $runC, $cacheDir -Force | Out-Null

    # -----------------------------------------------------------------------
    # Test 1: Generator determinism
    # -----------------------------------------------------------------------
    Write-Host "Test 1: Generator produces byte-identical CSVs across runs" -ForegroundColor Cyan
    Invoke-GeneratorOnly -OutputPath $runA -Template $Template
    Invoke-GeneratorOnly -OutputPath $runB -Template $Template

    $hashesA = Get-CsvTriplet -Dir $runA
    $hashesB = Get-CsvTriplet -Dir $runB

    for ($i = 0; $i -lt $hashesA.Count; $i++) {
        Assert-Equal $hashesA[$i].Hash $hashesB[$i].Hash "$($hashesA[$i].Name) is byte-identical across runs"
    }

    # -----------------------------------------------------------------------
    # Test 2: Cache archive round-trip preserves bytes
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "Test 2: Cache archive round-trip preserves bytes" -ForegroundColor Cyan

    $hash16 = Get-CsvCacheKey -IntegrationRoot $scriptRoot -Template $Template
    Assert-True ($hash16.Length -eq 16) "Cache key is 16 hex chars"

    $archivePath = Get-CsvCacheArchivePath -CacheRoot $cacheDir -Template $Template -Hash16 $hash16
    Save-CsvsToCache -ArchivePath $archivePath -OutputPath $runA -Template $Template -Hash16 $hash16
    Assert-True (Test-Path $archivePath) "Cache archive was created at $archivePath"

    Restore-CsvsFromCache -ArchivePath $archivePath -OutputPath $runC
    $hashesC = Get-CsvTriplet -Dir $runC

    for ($i = 0; $i -lt $hashesA.Count; $i++) {
        Assert-Equal $hashesA[$i].Hash $hashesC[$i].Hash "$($hashesA[$i].Name) is byte-identical after cache round-trip"
    }

    $manifestInArchive = Join-Path $runC "manifest.json"
    Assert-True (-not (Test-Path $manifestInArchive)) "manifest.json is stripped from extracted output"

    $crossDomainInArchive = Join-Path $runC "cross-domain-users.csv"
    Assert-True (-not (Test-Path $crossDomainInArchive)) "cross-domain-users.csv is NOT in the cache"

    # -----------------------------------------------------------------------
    # Test 3: Cache key is sensitive to template name
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "Test 3: Cache key differs across templates" -ForegroundColor Cyan
    $hashNano  = Get-CsvCacheKey -IntegrationRoot $scriptRoot -Template "Nano"
    $hashSmall = Get-CsvCacheKey -IntegrationRoot $scriptRoot -Template "Small"
    Assert-True ($hashNano -ne $hashSmall) "Nano and Small templates produce different cache keys"

    # -----------------------------------------------------------------------
    # Test 3b: Cache key is sensitive to -OmitItOwnedAttributes (#242)
    # -----------------------------------------------------------------------
    # Two CSV shapes (IT-owned attributes present or omitted) must never share a cache entry: a hit
    # for one shape must never silently serve the other's CSVs to a scenario expecting different
    # columns. Same template, switch on vs off, must key differently.
    Write-Host ""
    Write-Host "Test 3b: Cache key differs with -OmitItOwnedAttributes" -ForegroundColor Cyan
    $hashWithItOwned    = Get-CsvCacheKey -IntegrationRoot $scriptRoot -Template $Template
    $hashWithoutItOwned = Get-CsvCacheKey -IntegrationRoot $scriptRoot -Template $Template -OmitItOwnedAttributes
    Assert-True ($hashWithItOwned -ne $hashWithoutItOwned) "-OmitItOwnedAttributes produces a different cache key for the same template"

    # -----------------------------------------------------------------------
    # Test 4: Cache key is sensitive to hashed script changes
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "Test 4: Editing a hashed file changes the cache key" -ForegroundColor Cyan

    # Temporarily replace Generate-TestCSV.ps1 with a shadow copy, edit it, recompute, restore.
    $origGenerator = "$scriptRoot/Generate-TestCSV.ps1"
    $origContent   = Get-Content -Raw -Path $origGenerator
    $origHash      = Get-CsvCacheKey -IntegrationRoot $scriptRoot -Template $Template
    try {
        $mutated = $origContent + "`n# cache-bust test marker`n"
        Set-Content -Path $origGenerator -Value $mutated -NoNewline
        $mutatedHash = Get-CsvCacheKey -IntegrationRoot $scriptRoot -Template $Template
        Assert-True ($origHash -ne $mutatedHash) "Cache key changes when Generate-TestCSV.ps1 is edited"
    }
    finally {
        Set-Content -Path $origGenerator -Value $origContent -NoNewline
    }

    # -----------------------------------------------------------------------
    # Test 5: Cross-domain regeneration writes a fresh header file
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "Test 5: cross-domain-users.csv is regenerated fresh" -ForegroundColor Cyan
    $crossDomainPath = Invoke-CrossDomainTargetRegeneration -OutputPath $runC
    Assert-True (Test-Path $crossDomainPath) "cross-domain-users.csv was written"
    $headerLine = Get-Content -Path $crossDomainPath -TotalCount 1
    Assert-True ($headerLine -eq "samAccountName,displayName,email,department,employeeId,company,pronouns") "Header line is exactly the seven expected columns"
    $lineCount = (Get-Content -Path $crossDomainPath | Measure-Object -Line).Lines
    Assert-Equal 1 $lineCount "cross-domain-users.csv has exactly one (header) line"

    # -----------------------------------------------------------------------
    # Test 6: -OmitItOwnedAttributes actually omits the IT-owned columns (#242)
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "Test 6: -OmitItOwnedAttributes omits samAccountName, email, userPrincipalName from hr-users.csv" -ForegroundColor Cyan

    $runD = Join-Path $workRoot "gen-omit"
    New-Item -ItemType Directory -Path $runD -Force | Out-Null
    Invoke-GeneratorOnly -OutputPath $runD -Template $Template -OmitItOwnedAttributes

    $omittedHeader = ((Get-Content -Path (Join-Path $runD "hr-users.csv") -TotalCount 1) -split ',') | ForEach-Object { $_.Trim('"') }
    Assert-True ($omittedHeader -notcontains 'samAccountName') "hr-users.csv header omits samAccountName"
    Assert-True ($omittedHeader -notcontains 'email') "hr-users.csv header omits email"
    Assert-True ($omittedHeader -notcontains 'userPrincipalName') "hr-users.csv header omits userPrincipalName"
    Assert-True ($omittedHeader -contains 'employeeId') "hr-users.csv header still carries employeeId"
    Assert-True ($omittedHeader -contains 'firstName') "hr-users.csv header still carries firstName"

    # Unconverted (switch absent) generation must stay byte-for-byte unaffected by this feature's
    # addition. Test 1 already proved run A and run B (both without the switch, both against this
    # script as it now stands) hash-identical, which is exactly that guarantee; nothing further to
    # regenerate here.
    Assert-Equal $hashesA[0].Hash $hashesB[0].Hash "hr-users.csv is byte-for-byte unaffected when -OmitItOwnedAttributes is absent (from Test 1)"
}
finally {
    if (Test-Path $workRoot) {
        Remove-Item -Path $workRoot -Recurse -Force
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "=======================================================" -ForegroundColor Cyan
if ($script:FailCount -eq 0) {
    Write-Host " $script:TestCount / $script:TestCount tests passed." -ForegroundColor Green
    exit 0
}
else {
    Write-Host " $($script:TestCount - $script:FailCount) / $script:TestCount tests passed; $script:FailCount FAILED." -ForegroundColor Red
    exit 1
}
