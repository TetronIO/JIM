# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Freezes the migrations shipping in a release into the released-migrations manifest.

.DESCRIPTION
    Appends every EF Core migration not yet listed in
    src/JIM.PostgresData/Migrations/released-migrations.lock to that manifest, recording its id, the
    SHA-256 of its .cs and .Designer.cs files (line endings normalised to LF, matching
    ReleasedMigrationManifest.HashContent in JIM.Worker.Tests), and the version being released.

    A listed migration has been applied to customer databases and is immutable;
    ReleasedMigrationImmutabilityTests fails the build if one is renamed, edited or deleted, or if a
    new migration's timestamp sorts before the newest listed id. This script is the manifest's only
    sanctioned writer and is run by /release after the VERSION file is updated. It is idempotent: a
    re-run for the same version appends nothing. It refuses to run if an already-listed migration's
    hashes no longer match, because that is the very violation the manifest exists to prevent, and
    freezing over it would legitimise the edit.

.PARAMETER Version
    The version being released (e.g. 1.0.0 or 1.0.0-alpha), recorded against each newly frozen migration.

.PARAMETER MigrationsPath
    The migrations directory. Defaults to src/JIM.PostgresData/Migrations relative to the repository root.

.EXAMPLE
    ./scripts/Update-ReleasedMigrationsManifest.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')]
    [string]$Version,

    [string]$MigrationsPath = (Join-Path $PSScriptRoot '..' 'src' 'JIM.PostgresData' 'Migrations')
)

$ErrorActionPreference = 'Stop'

$migrationsDirectory = (Resolve-Path $MigrationsPath).Path
$manifestPath = Join-Path $migrationsDirectory 'released-migrations.lock'
if (-not (Test-Path $manifestPath)) {
    throw "Released-migrations manifest not found at $manifestPath; it is append-only and must never be deleted."
}

# Must match ReleasedMigrationManifest.HashContent: LF-normalised UTF-8 (no BOM in the hashed bytes;
# Get-Content -Raw already excludes a detected BOM from the string), SHA-256, lowercase hex.
function Get-NormalisedContentHash {
    param([string]$Path)
    $content = (Get-Content -Path $Path -Raw).Replace("`r`n", "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($content)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

# Parse the existing manifest: id -> @(csHash, designerHash, version).
$listed = [ordered]@{}
foreach ($line in Get-Content -Path $manifestPath) {
    $trimmed = $line.Trim()
    if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
    $fields = $trimmed -split ' +'
    if ($fields.Count -ne 4) {
        throw "Malformed manifest line (expected '<id> <hash> <designer hash> <version>'): '$trimmed'"
    }
    $listed[$fields[0]] = $fields[1..3]
}

$newEntries = @()
$migrationFiles = Get-ChildItem -Path $migrationsDirectory -Filter '*.cs' |
    Where-Object { $_.Name -match '^\d{14}_.+\.cs$' -and $_.Name -notlike '*.Designer.cs' } |
    Sort-Object Name

foreach ($file in $migrationFiles) {
    $id = $file.BaseName
    $designerPath = Join-Path $migrationsDirectory "$id.Designer.cs"
    if (-not (Test-Path $designerPath)) {
        throw "$id has no Designer file beside it."
    }

    $csHash = Get-NormalisedContentHash -Path $file.FullName
    $designerHash = Get-NormalisedContentHash -Path $designerPath

    if ($listed.Contains($id)) {
        $frozen = $listed[$id]
        if ($frozen[0] -ne $csHash -or $frozen[1] -ne $designerHash) {
            throw "$id is frozen (released in v$($frozen[2])) but its files have changed; released migrations are " +
                  'immutable and this script will not re-freeze an edit. Restore the shipped content.'
        }
        continue
    }

    $newEntries += "$id $csHash $designerHash $Version"
}

if ($newEntries.Count -eq 0) {
    Write-Host "No unlisted migrations; the manifest already covers all $($migrationFiles.Count) migrations."
    return
}

Add-Content -Path $manifestPath -Value $newEntries -Encoding utf8
Write-Host "Froze $($newEntries.Count) migration(s) at v$($Version):"
$newEntries | ForEach-Object { Write-Host "  $(($_ -split ' ')[0])" }
