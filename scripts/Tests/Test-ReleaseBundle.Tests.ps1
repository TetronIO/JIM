# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Test-ReleaseBundle.ps1.

.DESCRIPTION
    Builds small release archives by hand, each shaped as Build-ReleaseBundle.ps1 writes them, and checks the gate
    passes a sound one and stops a broken one, naming what is wrong. The checks are the customer's own commands,
    sha256sum and tar, so these tests need both and are skipped without them.
#>

BeforeDiscovery {
    $script:NoTools = -not ((Get-Command sha256sum -ErrorAction SilentlyContinue) -and (Get-Command tar -ErrorAction SilentlyContinue))
}

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Test-ReleaseBundle.ps1')).Path

    function Get-FileSha256 {
        param([string]$Path)
        (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    # A release archive, jim-release-1.2.3.tar.gz, and its checksum beside it. Its checksums.sha256 lists each file,
    # those in -Listed included, with -PathPrefix before its path; the files in -Unlisted are added after the list is
    # written; and -Corrupt changes the archive after its checksum is written.
    function New-ReleaseArchive {
        param(
            [string]$PathPrefix = '',
            [string[]]$Listed = @(),
            [string[]]$Unlisted = @(),
            [switch]$Corrupt,
            [switch]$NoArchiveChecksum
        )
        $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bundle = Join-Path $root 'jim-release-1.2.3'
        $files = @{
            'setup.sh' = "#!/usr/bin/env bash`necho install`n"
            'VERSION' = "1.2.3`n"
            'compose/docker-compose.yml' = "services: {}`n"
            'powershell/JIM/JIM.psd1' = "@{}`n"
        }
        foreach ($name in $Listed) {
            $files[$name] = "a listed file`n"
        }
        foreach ($name in $files.Keys) {
            $path = Join-Path $bundle $name
            New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
            $files[$name] | Set-Content -NoNewline $path
        }
        $lines = foreach ($name in ($files.Keys | Sort-Object)) {
            "$(Get-FileSha256 (Join-Path $bundle $name))  $PathPrefix$name"
        }
        (($lines -join "`n") + "`n") | Set-Content -NoNewline (Join-Path $bundle 'checksums.sha256')
        foreach ($name in $Unlisted) {
            $path = Join-Path $bundle $name
            New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
            'added later' | Set-Content -NoNewline $path
        }

        $archive = Join-Path $root 'jim-release-1.2.3.tar.gz'
        tar -czf $archive -C $root 'jim-release-1.2.3'
        if (-not $NoArchiveChecksum) {
            "$(Get-FileSha256 $archive)  jim-release-1.2.3.tar.gz`n" | Set-Content -NoNewline "$archive.sha256"
        }
        if ($Corrupt) {
            [IO.File]::AppendAllText($archive, 'tampered')
        }
        $archive
    }
}

Describe 'Test-ReleaseBundle' -Skip:$script:NoTools {
    It 'passes a release whose archive and every file in it match their checksums' {
        $archive = New-ReleaseArchive

        { & $script:ScriptPath -ArchivePath $archive 6>$null } | Should -Not -Throw
    }

    It 'stops when the bundle''s checksums name its files by any path but their own within it' {
        # Every release up to v0.16.0 listed its files under the build runner's folders (#1942).
        $archive = New-ReleaseArchive -PathPrefix 'JIM/release-output/jim-release-1.2.3/'

        { & $script:ScriptPath -ArchivePath $archive 6>$null } | Should -Throw '*checksums.sha256*JIM/release-output/jim-release-1.2.3/setup.sh*'
    }

    It 'stops when the archive does not match its own checksum' {
        $archive = New-ReleaseArchive -Corrupt

        { & $script:ScriptPath -ArchivePath $archive 6>$null } | Should -Throw '*jim-release-1.2.3.tar.gz*'
    }

    It 'stops when there is no checksum for the archive' {
        $archive = New-ReleaseArchive -NoArchiveChecksum

        { & $script:ScriptPath -ArchivePath $archive 6>$null } | Should -Throw '*jim-release-1.2.3.tar.gz.sha256*'
    }

    It 'stops when the bundle holds a file its checksums do not list, naming it' {
        $archive = New-ReleaseArchive -Unlisted 'docs/notes.txt'

        { & $script:ScriptPath -ArchivePath $archive 6>$null } | Should -Throw '*docs/notes.txt*'
    }

    It 'stops when the bundle holds a development-only file, naming it' {
        $archive = New-ReleaseArchive -Listed 'powershell/JIM/CLAUDE.md'

        { & $script:ScriptPath -ArchivePath $archive 6>$null } | Should -Throw '*powershell/JIM/CLAUDE.md*'
    }
}
