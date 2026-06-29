# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Initialize-WorkerLogDirectories and its writability probe.

.DESCRIPTION
    Guards the integration-test log directory setup that prevents the Docker daemon
    auto-creating the worker log bind-mount source as root on Linux hosts. See
    Initialize-WorkerLogDirectories.ps1 for the full rationale.
#>

BeforeAll {
    . "$PSScriptRoot/Initialize-WorkerLogDirectories.ps1"
}

Describe 'Initialize-WorkerLogDirectories' {
    BeforeEach {
        $script:tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "jim-logdir-$([System.Guid]::NewGuid())"
        $script:logDir = Join-Path $script:tempRoot 'results' 'logs'
        $script:workerDir = Join-Path $script:logDir 'worker'
    }

    AfterEach {
        if (Test-Path $script:tempRoot) {
            # Restore write perms first so cleanup of any 0500 test dirs succeeds.
            if ($IsLinux -or $IsMacOS) { & chmod -R u+rwx $script:tempRoot 2>$null }
            Remove-Item $script:tempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'creates the logs directory and the worker sub-directory when absent' {
        Initialize-WorkerLogDirectories -LogDirectory $script:logDir
        Test-Path $script:logDir   | Should -BeTrue
        Test-Path $script:workerDir | Should -BeTrue
    }

    It 'is idempotent when the directories already exist' {
        New-Item -ItemType Directory -Path $script:workerDir -Force | Out-Null
        { Initialize-WorkerLogDirectories -LogDirectory $script:logDir } | Should -Not -Throw
        Test-Path $script:workerDir | Should -BeTrue
    }

    It 'leaves the parent logs directory writable by the current user' {
        Initialize-WorkerLogDirectories -LogDirectory $script:logDir
        $probe = Join-Path $script:logDir 'transcript-probe.tmp'
        { [System.IO.File]::WriteAllText($probe, 'x'); Remove-Item $probe -Force } | Should -Not -Throw
    }

    It 'makes the worker directory world-writable on Linux so the non-root worker UID can write' -Skip:(-not $IsLinux) {
        Initialize-WorkerLogDirectories -LogDirectory $script:logDir
        (& stat -c '%a' $script:workerDir).Trim() | Should -Be '777'
    }

    It 'throws an actionable error when a directory cannot be made writable' -Skip:(-not ($IsLinux -or $IsMacOS)) {
        # Mirror the real failure: the directories already exist (as they would after a
        # non-runner stack-up) but the parent is not writable by the current user. We cannot
        # reproduce true root-ownership without sudo, so a 0500 parent stands in; it exercises
        # the same fail-fast probe branch. Both dirs pre-exist so New-Item is a clean no-op.
        New-Item -ItemType Directory -Path $script:workerDir -Force | Out-Null
        & chmod 0500 $script:logDir 2>$null
        { Initialize-WorkerLogDirectories -LogDirectory $script:logDir } |
            Should -Throw -ExpectedMessage '*not writable*'
    }
}

Describe 'Test-PathWritable' {
    BeforeEach {
        $script:tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "jim-writable-$([System.Guid]::NewGuid())"
        New-Item -ItemType Directory -Path $script:tempDir -Force | Out-Null
    }

    AfterEach {
        if (Test-Path $script:tempDir) {
            if ($IsLinux -or $IsMacOS) { & chmod -R u+rwx $script:tempDir 2>$null }
            Remove-Item $script:tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'returns true for a writable directory' {
        Test-PathWritable -Path $script:tempDir | Should -BeTrue
    }

    It 'returns false for a non-writable directory' -Skip:(-not ($IsLinux -or $IsMacOS)) {
        & chmod 0500 $script:tempDir 2>$null
        Test-PathWritable -Path $script:tempDir | Should -BeFalse
    }
}
