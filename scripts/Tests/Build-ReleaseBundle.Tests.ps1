# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Build-ReleaseBundle.ps1.

.DESCRIPTION
    Covers the options CI uses to bundle images it has already built: -SkipImageBuild exports the images
    already present rather than building them, and refuses to bundle one that is missing; -SkipArchive leaves
    the bundle as a folder. Docker is replaced by a function that records each call, so the tests need no
    Docker and build nothing.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Build-ReleaseBundle.ps1')).Path

    function New-OutputPath {
        Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
    }

    # Stands in for the docker command, which the script resolves to this function before any executable.
    # Records each call, writes an empty archive for "docker save -o <path>", and reports an image as missing
    # when its name is in $global:BundleTestMissingImages. Its state is global: called from the script under
    # test, $script: would mean that script's scope.
    function docker {
        $global:BundleTestDockerCalls.Add($args -join ' ')
        $global:LASTEXITCODE = 0
        if ($args[0] -eq 'save') {
            New-Item -ItemType File -Path $args[2] -Force | Out-Null
        }
        if ($args[0] -eq 'image' -and $args[1] -eq 'inspect' -and $global:BundleTestMissingImages -contains $args[2]) {
            $global:LASTEXITCODE = 1
        }
    }
}

Describe 'Build-ReleaseBundle' {
    BeforeEach {
        $global:BundleTestDockerCalls = [System.Collections.Generic.List[string]]::new()
        $global:BundleTestMissingImages = @()
    }

    Describe '-SkipImageBuild' {
        It 'exports the images already present, without building any' {
            $output = New-OutputPath

            & $script:ScriptPath -Version 1.2.3 -OutputPath $output -IncludePostgres $false -SkipImageBuild -SkipArchive 6>$null

            $global:BundleTestDockerCalls | Where-Object { $_ -like 'build*' } | Should -BeNullOrEmpty
            $global:BundleTestDockerCalls | Where-Object { $_ -like 'save*' } | Should -Be @(
                "save -o $output/jim-release-1.2.3/docker-images/jim-web.tar ghcr.io/tetronio/jim-web:1.2.3"
                "save -o $output/jim-release-1.2.3/docker-images/jim-worker.tar ghcr.io/tetronio/jim-worker:1.2.3"
                "save -o $output/jim-release-1.2.3/docker-images/jim-scheduler.tar ghcr.io/tetronio/jim-scheduler:1.2.3"
            )
        }

        It 'refuses to bundle an image that is not present, naming it' {
            $output = New-OutputPath
            $global:BundleTestMissingImages = @('ghcr.io/tetronio/jim-worker:1.2.3')

            { & $script:ScriptPath -Version 1.2.3 -OutputPath $output -IncludePostgres $false -SkipImageBuild -SkipArchive 6>$null } |
                Should -Throw '*ghcr.io/tetronio/jim-worker:1.2.3*'
        }

        It 'still builds the images without the switch' {
            $output = New-OutputPath

            & $script:ScriptPath -Version 1.2.3 -OutputPath $output -IncludePostgres $false -SkipArchive 6>$null

            @($global:BundleTestDockerCalls | Where-Object { $_ -like 'build*' }).Count | Should -Be 3
        }
    }

    Describe '-SkipArchive' {
        It 'leaves the bundle as a folder, with its installer and checksums, and writes no archive' {
            $output = New-OutputPath

            & $script:ScriptPath -Version 1.2.3 -OutputPath $output -IncludePostgres $false -SkipImageBuild -SkipArchive 6>$null

            Join-Path $output 'jim-release-1.2.3/setup.sh' | Should -Exist
            Join-Path $output 'jim-release-1.2.3/checksums.sha256' | Should -Exist
            Join-Path $output 'jim-release-1.2.3.tar.gz' | Should -Not -Exist
        }
    }
}

AfterAll {
    Remove-Variable -Name BundleTestDockerCalls, BundleTestMissingImages -Scope Global -ErrorAction SilentlyContinue
}
