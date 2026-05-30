# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Detects when newer versions of the apt packages pinned in production
    Dockerfiles are available in the base image's archive, and (optionally)
    rewrites the pins in place so a bump can be proposed for evaluation.

.DESCRIPTION
    Production Dockerfiles install a small set of OS packages with exact version
    pins (e.g. `libldap2=2.6.10+dfsg-0ubuntu0.24.04.1`) for reproducible builds.
    These pins are NOT visible to Dependabot (its docker ecosystem only parses
    FROM lines, not `pkg=version` inside RUN) and are NOT seen by the CI base
    image Trivy scan (the packages are added on top of the base, so they only
    exist in the built JIM image, which CI does not scan). The result is that a
    newer libldap2 / cifs-utils / krb5 (security or otherwise) can sit unnoticed
    until release time.

    This script closes that gap:

      1. Discovers production Dockerfiles (the `# jim-compliance: production-image`
         directive, same convention as discover-base-images.ps1).
      2. Parses each pinned `pkg=version` line and associates it with the base
         image of the build stage it is installed in (resolving stage aliases).
      3. For each base image, pulls it and queries the archive Candidate version
         for every pinned package (`apt-cache policy`). A pin is "behind" when the
         Candidate is strictly greater than the pin (`dpkg --compare-versions`).
      4. Validates that the Candidate is actually installable in that base image
         (`apt-get install --dry-run`), so a proposed bump is one we have proven
         resolvable, not just a version string. This matters because CI does not
         build the JIM images on a PR; the bot must not propose an unbuildable pin.

    Default mode reports findings as a table. `-Apply` additionally rewrites the
    validated bumps into the Dockerfiles in place (used by the apt-pin-check
    workflow to produce a PR for evaluation).

    Runnable locally from the repository root for ad hoc checks:

        pwsh -NoProfile -File .github/scripts/check-apt-pins.ps1

    Requires Docker (to pull and query the base images). Images are queried on
    linux/amd64 to match the shipped images regardless of the host architecture.

.PARAMETER Apply
    Rewrite validated bumps into the Dockerfiles in place. Without it, the script
    only reports.

.NOTES
    Exit codes:
      0  success; no updates available (or -Apply applied them cleanly)
      1  an error occurred (Docker unavailable, parse failure, etc.)
      2  updates are available (reporting mode only; lets a scheduled check or
         workflow branch on "is there anything to evaluate")
#>

[CmdletBinding()]
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

# linux/amd64 matches the shipped images; the dev host may be arm64.
$platform = 'linux/amd64'

function Test-DockerAvailable {
    $null = Get-Command docker -ErrorAction SilentlyContinue
    if (-not $?) { throw 'docker is not on PATH; this script needs Docker to query base images.' }
}

# --- Discover production Dockerfiles and parse their apt pins ----------------

$repoRoot = (Get-Location).Path

$dockerfiles = Get-ChildItem -Path $repoRoot -Recurse -File -Filter 'Dockerfile' -Force |
    Where-Object {
        $_.FullName -notmatch '[\\/]node_modules[\\/]' -and
        $_.FullName -notmatch '[\\/]\.git[\\/]' -and
        $_.FullName -notmatch '[\\/]bin[\\/]' -and
        $_.FullName -notmatch '[\\/]obj[\\/]'
    }

# Each pin: dockerfile (relative path), package, version, the resolved external
# base image of the stage it is installed in.
$pins = @()

foreach ($dockerfile in $dockerfiles) {
    $content = Get-Content -Path $dockerfile.FullName -Raw
    if ($content -notmatch '(?m)^#\s*jim-compliance:\s*production-image\s*$') { continue }

    $relativePath = [IO.Path]::GetRelativePath($repoRoot, $dockerfile.FullName) -replace '\\', '/'

    # Walk the file, tracking the current stage's external base image so each pin
    # is attributed to the image it is actually installed into. Stage aliases
    # (FROM x AS y) are resolved transitively to their external image ref.
    $aliasToImage = @{}
    $currentImage = $null

    foreach ($line in ($content -split "`n")) {
        if ($line -match '^\s*FROM\s+(\S+)(?:\s+AS\s+(\S+))?') {
            $fromRef = $matches[1]
            $alias   = $matches[2]
            $resolved = if ($aliasToImage.ContainsKey($fromRef)) { $aliasToImage[$fromRef] } else { $fromRef }
            $currentImage = $resolved
            if ($alias) { $aliasToImage[$alias] = $resolved }
            continue
        }

        # Pinned apt package: a line that is just "<pkg>=<version>" (optionally
        # with a trailing backslash). Package names are lower-case; versions
        # contain at least one digit. This deliberately ignores unpinned packages
        # (no '=') and shell assignments like ENV/ARG (uppercase / not bare).
        if ($line -match '^\s*([a-z][a-z0-9.+-]+)=([^\s\\]+)\s*\\?\s*$') {
            $pkg = $matches[1]
            $ver = $matches[2]
            if ($ver -notmatch '\d') { continue }
            if (-not $currentImage -or $currentImage -eq 'scratch' -or $currentImage -match '^\$\{') { continue }
            $pins += [pscustomobject]@{
                dockerfile = $relativePath
                package    = $pkg
                version    = $ver
                image_ref  = $currentImage
            }
        }
    }
}

if ($pins.Count -eq 0) {
    Write-Host 'No pinned apt packages found in any production Dockerfile. Nothing to check.'
    exit 0
}

Write-Host "Discovered $($pins.Count) pinned apt package(s) across $(@($pins | Select-Object -ExpandProperty dockerfile -Unique).Count) Dockerfile(s):"
foreach ($p in $pins) { Write-Host "  $($p.dockerfile): $($p.package)=$($p.version)  [$($p.image_ref)]" }
Write-Host ''

Test-DockerAvailable

# --- Query each base image's archive for candidate versions -----------------

# In-container script: for each "pkg|pinned" spec, emit one
# "RESULT|pkg|pinned|candidate|newer|installable|security" line. Runs as root
# (the .NET base images default to a non-root user, which cannot run apt).
$containerScript = @'
set -u
apt-get update -qq >/dev/null 2>&1 || { echo "APTUPDATEFAILED"; exit 0; }
for spec in "$@"; do
  pkg="${spec%%|*}"; pinned="${spec##*|}"
  cand="$(apt-cache policy "$pkg" 2>/dev/null | awk -F': ' '/Candidate:/{print $2}' | tr -d ' ')"
  if [ -z "$cand" ] || [ "$cand" = "(none)" ]; then
    echo "RESULT|$pkg|$pinned|none|no|skip|no"; continue
  fi
  newer=no
  if dpkg --compare-versions "$cand" gt "$pinned" 2>/dev/null; then newer=yes; fi
  security=no
  if apt-cache policy "$pkg" 2>/dev/null | grep -q -- '-security'; then security=yes; fi
  installable=skip
  if [ "$newer" = yes ]; then
    if apt-get install -y --no-install-recommends --dry-run "$pkg=$cand" >/dev/null 2>&1; then
      installable=yes
    else
      installable=no
    fi
  fi
  echo "RESULT|$pkg|$pinned|$cand|$newer|$installable|$security"
done
'@

# Normalise to LF: this file may be checked out with CRLF line endings (git
# autocrlf on .ps1), and carriage returns inside the script break bash when it
# is passed via `bash -c` ("syntax error near unexpected token $'do\r'").
$containerScript = $containerScript -replace "`r", ''

$results = @()
$queryFailures = @()

foreach ($group in ($pins | Group-Object image_ref)) {
    $imageRef = $group.Name
    Write-Host "Querying $imageRef ..."
    # Best-effort pull, then confirm the image is actually present. We do not key
    # off the pull exit code: some daemon configurations report a non-zero
    # "cannot overwrite digest" when a digest-pinned image is already cached,
    # which is not a failure.
    docker pull --platform $platform $imageRef *> $null
    docker image inspect $imageRef *> $null
    if ($LASTEXITCODE -ne 0) { throw "base image not available locally and could not be pulled: $imageRef" }

    $specs = @($group.Group | ForEach-Object { "$($_.package)|$($_.version)" })

    # Pass the script as a `bash -c` argument with the specs as positional
    # parameters ($@). This is more portable than piping the script on stdin
    # (`bash -s`), which silently delivered nothing on the GitHub-hosted runner.
    # Capture stderr too so a container failure is diagnosable rather than silent.
    $raw = docker run --rm --platform $platform --user root --entrypoint bash $imageRef -c $containerScript -- @specs 2>&1 | Out-String

    $rowCount = 0
    foreach ($rln in ($raw -split "`n")) {
        if ($rln -notmatch '^RESULT\|') { continue }
        $rowCount++
        $f = $rln.Trim() -split '\|'
        $results += [pscustomobject]@{
            image_ref   = $imageRef
            package     = $f[1]
            pinned      = $f[2]
            candidate   = $f[3]
            newer       = ($f[4] -eq 'yes')
            installable = $f[5]
            security    = ($f[6] -eq 'yes')
        }
    }

    # A query that returns no rows for an image we have pins for means the check
    # did not actually run (e.g. apt-get update failed, or the container errored).
    # Treat that as a hard failure, never as "all current": a silent false
    # negative would leave the pins unmonitored while looking healthy.
    if ($rowCount -lt $specs.Count) {
        $queryFailures += $imageRef
        Write-Host "ERROR: expected $($specs.Count) result(s) from $imageRef but got $rowCount. Container output:"
        Write-Host ($raw.Trim())
    }
}

if ($queryFailures.Count -gt 0) {
    Write-Host ''
    Write-Host "FATAL: apt pin check could not complete for $($queryFailures.Count) base image(s): $($queryFailures -join ', ')"
    Write-Host 'Refusing to report a result; the pins are NOT confirmed current.'
    exit 1
}

# --- Report -----------------------------------------------------------------

$updates = @($results | Where-Object { $_.newer -and $_.installable -eq 'yes' })
$blocked = @($results | Where-Object { $_.newer -and $_.installable -eq 'no' })

Write-Host ''
Write-Host '== apt pin status =='
foreach ($r in $results) {
    $state = if (-not $r.newer) { 'current' }
             elseif ($r.installable -eq 'yes') { 'UPDATE' + ($(if ($r.security) { ' (security)' } else { '' })) }
             elseif ($r.installable -eq 'no') { 'update-not-installable' }
             else { 'unknown' }
    Write-Host ("  {0,-22} {1,-40} -> {2,-40} {3}" -f $r.package, $r.pinned, $r.candidate, $state)
}
Write-Host ''

if ($blocked.Count -gt 0) {
    Write-Host "WARNING: $($blocked.Count) package(s) have a newer candidate that did not resolve via apt (skipped, not proposed)."
}

if ($updates.Count -eq 0) {
    Write-Host 'All pinned apt packages are current. Nothing to evaluate.'
    exit 0
}

Write-Host "$($updates.Count) pinned apt package(s) have an installable update available to evaluate."

# Build a markdown body fragment for the PR, and expose machine-readable outputs.
$bodyLines = @('| Dockerfile | Package | Pinned | Available | Source |', '| --- | --- | --- | --- | --- |')
foreach ($u in $updates) {
    foreach ($pin in ($pins | Where-Object { $_.image_ref -eq $u.image_ref -and $_.package -eq $u.package })) {
        $src = if ($u.security) { '`-security`' } else { 'updates' }
        $bodyLines += "| $($pin.dockerfile) | $($u.package) | $($u.pinned) | $($u.candidate) | $src |"
    }
}
$body = $bodyLines -join "`n"

# Write the PR-body table to a file (consumed by open-apt-pin-pr.ps1). Writing a
# file rather than threading multi-line text through GITHUB_OUTPUT avoids
# here-string / indentation fragility in the workflow.
Set-Content -Path (Join-Path $repoRoot 'apt-pin-pr-body.md') -Value $body -Encoding utf8

if ($env:GITHUB_OUTPUT) {
    "has_updates=true" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "update_count=$($updates.Count)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

if ($Apply) {
    Write-Host ''
    Write-Host 'Applying validated bumps to Dockerfiles ...'
    foreach ($u in $updates) {
        foreach ($pin in ($pins | Where-Object { $_.image_ref -eq $u.image_ref -and $_.package -eq $u.package -and $_.version -eq $u.pinned })) {
            $path = Join-Path $repoRoot $pin.dockerfile
            $text = Get-Content -Path $path -Raw
            $old  = "$($u.package)=$($u.pinned)"
            $new  = "$($u.package)=$($u.candidate)"
            if ($text.Contains($old)) {
                $text = $text.Replace($old, $new)
                Set-Content -Path $path -Value $text -NoNewline
                Write-Host "  $($pin.dockerfile): $old -> $new"
            }
        }
    }
}

exit 2
