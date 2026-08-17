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
      5. Validates that each Dockerfile's pins, AS A SET, still resolve in its
         base image, which is the ground truth for "can this image still be
         built at all". Nothing else in CI answers that: build-and-test compiles
         and runs tests rather than building the production images, and the
         drift comes from the archive rather than from any commit. When Ubuntu
         published libgssapi-krb5-2 1.20.1-6ubuntu2.8 it withdrew 2.7, and
         `main` could not build jim-worker for two days with every required
         check green (#1374).

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
    Exit codes (the highest applicable one wins):
      0  success; no updates available (or -Apply applied them cleanly)
      1  an error occurred (Docker unavailable, parse failure, etc.)
      2  updates are available (reporting mode only; lets a scheduled check or
         workflow branch on "is there anything to evaluate")
      3  at least one Dockerfile's pinned set can no longer be installed in its
         base image, so that image cannot be built. Reported after any bumps have
         been applied and written out, so the workflow can still raise the PR
         that fixes it and then fail the run.
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

# In-container script: for each "pkg|pinned|dockerfile" spec, emit one
# "RESULT|pkg|pinned|candidate|newer|installable|security" line, then one
# "PINSET|dockerfile|yes|no|detail" line per Dockerfile saying whether that
# file's pins still resolve together. Runs as root (the .NET base images default
# to a non-root user, which cannot run apt).
#
# The pins are probed as a set, not one at a time, because that is what the image
# build does: a leaf pin can be individually resolvable and still unbuildable
# alongside its siblings, and vice versa.
$containerScript = @'
set -u
apt-get update -qq >/dev/null 2>&1 || { echo "APTUPDATEFAILED"; exit 0; }
specs="$*"
for spec in $specs; do
  pkg="$(printf '%s' "$spec" | cut -d'|' -f1)"
  pinned="$(printf '%s' "$spec" | cut -d'|' -f2)"
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
for df in $(for spec in $specs; do printf '%s\n' "$spec" | cut -d'|' -f3; done | sort -u); do
  pinargs=""
  for spec in $specs; do
    if [ "$(printf '%s' "$spec" | cut -d'|' -f3)" = "$df" ]; then
      pinargs="$pinargs $(printf '%s' "$spec" | cut -d'|' -f1)=$(printf '%s' "$spec" | cut -d'|' -f2)"
    fi
  done
  if err="$(apt-get install -y --no-install-recommends --dry-run $pinargs 2>&1)"; then
    echo "PINSET|$df|yes|"
  else
    # Keep the diagnosis (the unmet dependency lines and apt's E: summary) rather
    # than the first 300 characters, which are all boilerplate about impossible
    # situations and the unstable distribution.
    detail="$(printf '%s' "$err" | grep -E 'Depends:|Conflicts:|Breaks:|^E:' | tr '\n|' '  ' | cut -c1-300)"
    if [ -z "$detail" ]; then
      detail="$(printf '%s' "$err" | tr '\n|' '  ' | tail -c 300)"
    fi
    echo "PINSET|$df|no|$detail"
  fi
done
'@

# Normalise to LF: this file may be checked out with CRLF line endings (git
# autocrlf on .ps1), and carriage returns inside the script break bash when it
# is passed via `bash -c` ("syntax error near unexpected token $'do\r'").
$containerScript = $containerScript -replace "`r", ''

$results = @()
$pinSets = @()
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

    $specs = @($group.Group | ForEach-Object { "$($_.package)|$($_.version)|$($_.dockerfile)" })
    $groupDockerfiles = @($group.Group | Select-Object -ExpandProperty dockerfile -Unique)

    # Pass the script as a `bash -c` argument with the specs as positional
    # parameters ($@). This is more portable than piping the script on stdin
    # (`bash -s`), which silently delivered nothing on the GitHub-hosted runner.
    # Capture stderr too so a container failure is diagnosable rather than silent.
    $raw = docker run --rm --platform $platform --user root --entrypoint bash $imageRef -c $containerScript -- @specs 2>&1 | Out-String

    $rowCount = 0
    $pinSetCount = 0
    foreach ($rln in ($raw -split "`n")) {
        if ($rln -match '^RESULT\|') {
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
            continue
        }

        if ($rln -match '^PINSET\|') {
            $pinSetCount++
            $f = $rln.Trim() -split '\|', 4
            $pinSets += [pscustomobject]@{
                image_ref   = $imageRef
                dockerfile  = $f[1]
                installable = ($f[2] -eq 'yes')
                detail      = $f[3]
            }
        }
    }

    # A query that returns no rows for an image we have pins for means the check
    # did not actually run (e.g. apt-get update failed, or the container errored).
    # Treat that as a hard failure, never as "all current": a silent false
    # negative would leave the pins unmonitored while looking healthy. The same
    # goes for a missing pinned-set verdict.
    if ($rowCount -lt $specs.Count -or $pinSetCount -lt $groupDockerfiles.Count) {
        $queryFailures += $imageRef
        Write-Host "ERROR: expected $($specs.Count) result(s) and $($groupDockerfiles.Count) pinned-set verdict(s) from $imageRef but got $rowCount and $pinSetCount. Container output:"
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

# Writes the PR-body table and, under -Apply, rewrites the pins. Kept in a
# function so it can run before the unbuildable check decides the exit code: a
# base image that has dropped a pinned version needs BOTH the loud failure and
# the bump PR that fixes it, and a bare `exit 3` above this would suppress the
# very proposal that clears the failure.
function Publish-BumpProposal {
    $bodyLines = @('| Dockerfile | Package | Pinned | Available | Source |', '| --- | --- | --- | --- | --- |')
    foreach ($u in $updates) {
        foreach ($pin in ($pins | Where-Object { $_.image_ref -eq $u.image_ref -and $_.package -eq $u.package })) {
            $src = if ($u.security) { '`-security`' } else { 'updates' }
            $bodyLines += "| $($pin.dockerfile) | $($u.package) | $($u.pinned) | $($u.candidate) | $src |"
        }
    }

    # Write the PR-body table to a file (consumed by open-pin-pr.ps1). Writing a
    # file rather than threading multi-line text through GITHUB_OUTPUT avoids
    # here-string / indentation fragility in the workflow.
    Set-Content -Path (Join-Path $repoRoot 'apt-pin-pr-body.md') -Value ($bodyLines -join "`n") -Encoding utf8

    if (-not $Apply) { return }

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

# --- Can each image still be built at all? ----------------------------------

$unbuildable = @($pinSets | Where-Object { -not $_.installable })

Write-Host '== pinned set installability =='
foreach ($ps in $pinSets) {
    $verdict = if ($ps.installable) { 'resolves' } else { "CANNOT BE INSTALLED: $($ps.detail)" }
    Write-Host ("  {0,-32} {1}" -f $ps.dockerfile, $verdict)
}
Write-Host ''

if ($env:GITHUB_OUTPUT) {
    "has_updates=$($updates.Count -gt 0)".ToLowerInvariant() | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "update_count=$($updates.Count)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "unbuildable=$($unbuildable.Count -gt 0)".ToLowerInvariant() | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "unbuildable_dockerfiles=$(@($unbuildable | Select-Object -ExpandProperty dockerfile) -join ', ')" |
        Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

if ($updates.Count -eq 0) {
    Write-Host 'All pinned apt packages are current. Nothing to evaluate.'
    # Not `exit 0`: an image that cannot be built has to be reported whether or
    # not there is a bump to propose, and the exit-code decision is made once, at
    # the end of the script.
} else {
    Write-Host "$($updates.Count) pinned apt package(s) have an installable update available to evaluate."
    Publish-BumpProposal
}

if ($unbuildable.Count -gt 0) {
    Write-Host ''
    Write-Host "FATAL: $($unbuildable.Count) Dockerfile(s) pin a package set that can no longer be installed in the base image:"
    foreach ($ps in $unbuildable) { Write-Host "  $($ps.dockerfile) [$($ps.image_ref)]: $($ps.detail)" }
    Write-Host 'That image cannot be built from the current pins, so this is reported loudly rather than'
    Write-Host 'left to surface at release time. Land the bump above (or correct the pins by hand) to clear it.'
    exit 3
}

if ($updates.Count -eq 0) { exit 0 }
exit 2
