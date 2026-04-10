# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Discovers production Dockerfiles and their pinned base image references, and
    enforces the digest-pinning policy for them.

.DESCRIPTION
    Walks the repository for files named "Dockerfile". Any Dockerfile that contains
    the machine-readable directive "# jim-compliance: production-image" is treated
    as a production image, which means:

      1. Every external FROM line (i.e. not a "FROM <stage-alias>" intra-file
         reference, and not "FROM scratch") must be digest-pinned with @sha256:.
         Any unpinned reference is a policy violation and fails the script.

      2. All resolved image references are emitted as a matrix, deduplicated by
         image ref, so downstream jobs can scan each unique image exactly once.

    Dockerfiles that do NOT carry the compliance directive (e.g. the devcontainer
    image, test fixture images for Samba AD / OpenLDAP) are skipped entirely. They
    are not in scope for either the digest-pin policy or the vulnerability scan.

    Intended to run in CI (.github/workflows/ci.yml) but also runnable locally from
    the repository root for ad hoc verification:

        pwsh -NoProfile -File .github/scripts/discover-base-images.ps1

    Exits with 0 if everything is compliant, 1 if any policy violation is found or
    if no production Dockerfiles are discovered at all.

.NOTES
    This script is the source of truth for "which production base images are
    scanned". There is no parallel list anywhere else. Adding a new production
    Dockerfile means adding the "# jim-compliance: production-image" directive to
    the file itself and nothing else. See engineering/DEVELOPER_GUIDE.md "Docker
    Base Images" section for the full policy.

    Edge case not currently enforced: a production-labelled Dockerfile with zero
    external FROM lines will not be flagged by this script. Docker itself would
    refuse to build such a file, so the gap is covered at build time.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Location).Path
$results = @()
$policyViolations = @()

# Find every Dockerfile in the repo, including those in hidden directories like
# .devcontainer. Exclude build/tooling directories that may contain unrelated
# Dockerfiles (none expected today; the exclusions are defensive for future).
$dockerfiles = Get-ChildItem -Path $repoRoot -Recurse -File -Filter 'Dockerfile' -Force |
    Where-Object {
        $_.FullName -notmatch '[\\/]node_modules[\\/]' -and
        $_.FullName -notmatch '[\\/]\.claude[\\/]' -and
        $_.FullName -notmatch '[\\/]\.git[\\/]' -and
        $_.FullName -notmatch '[\\/]bin[\\/]' -and
        $_.FullName -notmatch '[\\/]obj[\\/]'
    }

Write-Host "Discovered $($dockerfiles.Count) Dockerfile(s)"

foreach ($dockerfile in $dockerfiles) {
    $relativePath = [IO.Path]::GetRelativePath($repoRoot, $dockerfile.FullName) -replace '\\', '/'
    $content = Get-Content -Path $dockerfile.FullName -Raw

    # Production label check: compact machine-readable directive on its own line.
    $isProduction = $content -match '(?m)^#\s*jim-compliance:\s*production-image\s*$'

    if (-not $isProduction) {
        Write-Host "SKIP: $relativePath (not labelled jim-compliance: production-image)"
        continue
    }

    Write-Host "SCAN: $relativePath"

    # Collect stage aliases defined by "FROM ... AS <alias>" so we can distinguish
    # intra-file stage references from external image references.
    $aliases = @()
    foreach ($line in ($content -split "`n")) {
        if ($line -match '^\s*FROM\s+\S+\s+AS\s+(\S+)') {
            $aliases += $matches[1]
        }
    }

    # Parse every FROM line in this Dockerfile.
    $lineNumber = 0
    foreach ($line in ($content -split "`n")) {
        $lineNumber++
        if ($line -notmatch '^\s*FROM\s+(\S+)') { continue }
        $imageRef = $matches[1]

        # Skip intra-file stage references (e.g. "FROM build AS final").
        if ($aliases -contains $imageRef) {
            Write-Host "  line ${lineNumber}: stage alias '$imageRef' (skipped)"
            continue
        }

        # Skip "FROM scratch".
        if ($imageRef -eq 'scratch') {
            Write-Host "  line ${lineNumber}: scratch (skipped)"
            continue
        }

        # Enforce the digest-pinning policy.
        if ($imageRef -notmatch '@sha256:[0-9a-f]{64}') {
            $policyViolations += "${relativePath}:${lineNumber}: FROM $imageRef is not digest-pinned"
            Write-Host "  line ${lineNumber}: $imageRef (POLICY VIOLATION: not digest-pinned)"
            continue
        }

        Write-Host "  line ${lineNumber}: $imageRef (ok)"
        $results += [pscustomobject]@{
            dockerfile = $relativePath
            line       = $lineNumber
            image_ref  = $imageRef
        }
    }
}

Write-Host ''

if ($policyViolations.Count -gt 0) {
    Write-Host '== POLICY VIOLATIONS =='
    foreach ($v in $policyViolations) { Write-Host "  $v" }
    Write-Host ''
    Write-Host 'Production Dockerfiles must digest-pin every external FROM line.'
    Write-Host 'See engineering/DEVELOPER_GUIDE.md "Docker Base Images" section.'
    exit 1
}

# Sanity check: a working repo must have at least one production Dockerfile. Zero
# means the compliance label has been accidentally removed from every production
# image, which is itself a regression to catch here rather than silently pass.
if ($results.Count -eq 0) {
    Write-Host 'No production-labelled Dockerfiles discovered.'
    Write-Host 'Expected at least one Dockerfile with "# jim-compliance: production-image".'
    Write-Host 'If this is intentional, update this script; otherwise check that the labels'
    Write-Host 'have not been accidentally removed from src/JIM.*/Dockerfile.'
    exit 1
}

# Deduplicate by image_ref: no point scanning the same digest twice.
$deduped = @($results | Sort-Object image_ref -Unique)
Write-Host "Unique image refs to scan: $($deduped.Count)"

foreach ($r in $deduped) {
    Write-Host "  $($r.image_ref)"
    Write-Host "    from $($r.dockerfile):$($r.line)"
}

# Build matrix JSON for GitHub Actions consumption.
$matrixObject = @{ include = $deduped }
$matrixJson = $matrixObject | ConvertTo-Json -Compress -Depth 4

Write-Host ''
Write-Host '== Matrix JSON =='
Write-Host $matrixJson

# If running in GitHub Actions, write to $GITHUB_OUTPUT so the scan job matrix can
# consume it via needs.discover-base-images.outputs.matrix.
if ($env:GITHUB_OUTPUT) {
    "matrix=$matrixJson" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    Write-Host ''
    Write-Host 'Wrote matrix to GITHUB_OUTPUT'
}
