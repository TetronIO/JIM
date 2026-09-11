# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for .github/scripts/discover-base-images.ps1.

.DESCRIPTION
    The script is the single source of truth for two things CI consumes: which
    production Dockerfiles exist (and that every external FROM line in them is
    digest-pinned), and the matrices the scan job builds from them. Two matrices
    come out:

      matrix        one leg per unique base image reference, keyed by the image
                    repository (the original output, kept for the pin policy
                    and for anything that still wants the base image list);

      image_matrix  one leg per production Dockerfile, naming the JIM image it
                    builds (jim-web, jim-worker, jim-scheduler), which is what
                    the vulnerability scan builds and scans. The built image is
                    what customers run: it alone carries the apt pins and the
                    build-time apt-get upgrade, neither of which a base image
                    scan can see.

    The script takes the current directory as the repository root, so each test
    builds a fixture tree under $TestDrive and runs the script from inside it.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'discover-base-images.ps1')).Path

    $script:Aspnet  = 'mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:' + ('a' * 64)
    $script:Runtime = 'mcr.microsoft.com/dotnet/runtime:10.0-noble@sha256:' + ('b' * 64)
    $script:Sdk     = 'mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:' + ('c' * 64)

    function New-ProductionDockerfile([string]$Base, [string]$Build) {
        return @(
            '# syntax=docker/dockerfile:1',
            '# jim-compliance: production-image',
            "FROM $Base AS base",
            'WORKDIR /app',
            "FROM $Build AS build",
            'FROM build AS publish',
            'FROM base AS final'
        ) -join "`n"
    }

    # Writes the fixture files (relative path -> content) under a fresh root, runs the script
    # from that root with GITHUB_OUTPUT pointed at a scratch file, and returns the exit code,
    # console output and the parsed outputs.
    function Invoke-Discovery([hashtable]$Files) {
        $root = Join-Path $TestDrive ("Repo_" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        foreach ($rel in $Files.Keys) {
            $full = Join-Path $root $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            Set-Content -Path $full -Value $Files[$rel] -NoNewline
        }

        $outputFile = Join-Path $root 'github-output.txt'
        $previousOutput = $env:GITHUB_OUTPUT
        $env:GITHUB_OUTPUT = $outputFile
        Push-Location $root
        try {
            $console = & $script:ScriptPath 6>&1 | Out-String
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
            $env:GITHUB_OUTPUT = $previousOutput
        }

        $outputs = @{}
        if (Test-Path $outputFile) {
            foreach ($line in Get-Content $outputFile) {
                if ($line -match '^([^=]+)=(.*)$') { $outputs[$Matches[1]] = $Matches[2] | ConvertFrom-Json }
            }
        }

        return [pscustomobject]@{ ExitCode = $exitCode; Output = $console; Outputs = $outputs }
    }
}

Describe 'discover-base-images.ps1 image matrix' {
    It 'emits one leg per production Dockerfile, named after the JIM image it builds' {
        $outcome = Invoke-Discovery @{
            'src/JIM.Web/Dockerfile'       = New-ProductionDockerfile $script:Aspnet $script:Sdk
            'src/JIM.Worker/Dockerfile'    = New-ProductionDockerfile $script:Runtime $script:Sdk
            'src/JIM.Scheduler/Dockerfile' = New-ProductionDockerfile $script:Runtime $script:Sdk
        }

        $outcome.ExitCode | Should -Be 0
        $legs = @($outcome.Outputs['image_matrix'].include)
        @($legs | ForEach-Object { $_.dockerfile }) | Should -Be @('src/JIM.Scheduler/Dockerfile', 'src/JIM.Web/Dockerfile', 'src/JIM.Worker/Dockerfile')
        @($legs | ForEach-Object { $_.image_name }) | Should -Be @('jim-scheduler', 'jim-web', 'jim-worker')
    }

    It 'leaves Dockerfiles without the compliance directive out of the image matrix' {
        $outcome = Invoke-Discovery @{
            'src/JIM.Web/Dockerfile'   = New-ProductionDockerfile $script:Aspnet $script:Sdk
            '.devcontainer/Dockerfile' = "FROM mcr.microsoft.com/devcontainers/dotnet:10.0`nRUN echo dev"
        }

        $outcome.ExitCode | Should -Be 0
        @($outcome.Outputs['image_matrix'].include | ForEach-Object { $_.image_name }) | Should -Be @('jim-web')
    }

    It 'still emits the base image matrix, deduplicated by image reference' {
        $outcome = Invoke-Discovery @{
            'src/JIM.Web/Dockerfile'    = New-ProductionDockerfile $script:Aspnet $script:Sdk
            'src/JIM.Worker/Dockerfile' = New-ProductionDockerfile $script:Runtime $script:Sdk
        }

        $refs = @($outcome.Outputs['matrix'].include | ForEach-Object { $_.image_ref })
        $refs | Should -Be @($script:Aspnet, $script:Runtime, $script:Sdk)
    }
}

Describe 'discover-base-images.ps1 digest-pinning policy' {
    It 'fails when a production Dockerfile has an unpinned external FROM line' {
        $outcome = Invoke-Discovery @{
            'src/JIM.Web/Dockerfile' = New-ProductionDockerfile 'mcr.microsoft.com/dotnet/aspnet:10.0-noble' $script:Sdk
        }

        $outcome.ExitCode | Should -Be 1
        $outcome.Output | Should -Match 'POLICY VIOLATION'
    }

    It 'fails when no production Dockerfile is found at all' {
        $outcome = Invoke-Discovery @{
            '.devcontainer/Dockerfile' = "FROM mcr.microsoft.com/devcontainers/dotnet:10.0"
        }

        $outcome.ExitCode | Should -Be 1
    }
}
