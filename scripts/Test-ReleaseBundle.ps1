# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Checks a release archive the way a customer checks it, before it is published.

.DESCRIPTION
    The release gate for the air-gapped bundle. Every release up to v0.16.0 shipped a checksums.sha256 that named
    each file under the build runner's folders, so the deployment guide's own check failed on every line for every
    customer, and nothing covered the archive itself (#1942). This runs the guide's commands, so that a release it
    passes is one whose checks pass for a customer:

      - sha256sum -c on the archive's checksum, jim-release-X.Y.Z.tar.gz.sha256, in the folder holding both;
      - sha256sum -c checksums.sha256 in the extracted bundle.

    It then checks what sha256sum cannot see: that checksums.sha256 lists every file the bundle holds, and that the
    bundle holds no development-only file. It reports every problem it finds, and fails if there is any.

.PARAMETER ArchivePath
    The release archive, jim-release-X.Y.Z.tar.gz, with its checksum file beside it.

.EXAMPLE
    ./scripts/Test-ReleaseBundle.ps1 -ArchivePath ./release-output/jim-release-0.17.0.tar.gz
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'

# Files that are only for developing JIM, which no bundle should carry.
$developmentOnlyNames = @('CLAUDE.md', '*.Tests.ps1')

# Runs sha256sum -c on a checksum file in the given folder, and returns the lines it did not report as OK.
function Get-ChecksumFailures {
    param([string]$Folder, [string]$ChecksumFile)
    Push-Location -LiteralPath $Folder
    try {
        $output = @(sha256sum -c $ChecksumFile 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE -eq 0) {
            return @()
        }
        # A missing file is reported twice, as "No such file or directory" and as "FAILED open or read"; keep the one.
        $failures = @($output | Where-Object { $_ -notmatch ': OK$' -and $_ -notmatch ': No such file or directory$' })
        if ($failures.Count -eq 0) {
            $failures = @("sha256sum exited with code $LASTEXITCODE")
        }
        $failures
    }
    finally {
        Pop-Location
    }
}

# A few lines of a long list, saying how many more there are.
function Format-Lines {
    param([string[]]$Lines, [int]$Limit = 20)
    $shown = @($Lines | Select-Object -First $Limit | ForEach-Object { "    $_" })
    if ($Lines.Count -gt $Limit) {
        $shown += "    ... and $($Lines.Count - $Limit) more"
    }
    $shown -join "`n"
}

$archive = Get-Item -LiteralPath $ArchivePath
$bundleName = $archive.Name -replace '\.tar\.gz$', ''
$archiveChecksum = "$($archive.Name).sha256"
$problems = [System.Collections.Generic.List[string]]::new()

Write-Host "Checking $($archive.Name) as a customer would" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath (Join-Path $archive.DirectoryName $archiveChecksum))) {
    $problems.Add("There is no checksum for the archive: $archiveChecksum is not beside $($archive.Name), so the download cannot be checked before it is carried into an air-gapped site.")
}
else {
    $failures = Get-ChecksumFailures -Folder $archive.DirectoryName -ChecksumFile $archiveChecksum
    if ($failures.Count -gt 0) {
        $problems.Add("sha256sum -c $archiveChecksum fails for $($archive.Name):`n$(Format-Lines $failures)")
    }
    else {
        Write-Host "  $($archive.Name) matches $archiveChecksum" -ForegroundColor Green
    }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    tar -xzf $archive.FullName -C $work
    $bundle = Join-Path $work $bundleName
    if ($LASTEXITCODE -ne 0) {
        $problems.Add("$($archive.Name) could not be extracted.")
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $bundle 'checksums.sha256'))) {
        $problems.Add("$($archive.Name) does not hold $bundleName/checksums.sha256.")
    }
    else {
        $failures = Get-ChecksumFailures -Folder $bundle -ChecksumFile 'checksums.sha256'
        if ($failures.Count -gt 0) {
            $problems.Add("sha256sum -c checksums.sha256 fails in the extracted bundle, as it would for a customer:`n$(Format-Lines $failures)")
        }
        else {
            Write-Host "  Every file listed in checksums.sha256 matches it" -ForegroundColor Green
        }

        # sha256sum checks only the files listed, so a file added after the list was written would go unchecked.
        $listed = @(Get-Content -LiteralPath (Join-Path $bundle 'checksums.sha256') | ForEach-Object {
                if ($_ -match '^[0-9a-f]{64} [ *](.+)$') { $Matches[1] }
            })
        $files = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | ForEach-Object {
                [IO.Path]::GetRelativePath($bundle, $_.FullName).Replace('\', '/')
            } | Where-Object { $_ -ne 'checksums.sha256' })
        $unlisted = @($files | Where-Object { $listed -notcontains $_ } | Sort-Object)
        if ($unlisted.Count -gt 0) {
            $problems.Add("The bundle holds files checksums.sha256 does not list, which no check covers:`n$(Format-Lines $unlisted)")
        }

        $developmentOnly = @($files | Where-Object {
                $name = [IO.Path]::GetFileName($_)
                @($developmentOnlyNames | Where-Object { $name -like $_ }).Count -gt 0
            } | Sort-Object)
        if ($developmentOnly.Count -gt 0) {
            $problems.Add("The bundle holds development-only files:`n$(Format-Lines $developmentOnly)")
        }
        else {
            Write-Host "  The bundle holds no development-only files" -ForegroundColor Green
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($problems.Count -gt 0) {
    throw "The release archive $($archive.Name) fails the checks a customer runs:`n`n$($problems -join "`n`n")"
}
Write-Host "$($archive.Name) passes the checks a customer runs" -ForegroundColor Green
