# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the structure of .github/workflows/release.yml.

.DESCRIPTION
    Each Docker-using job on the self-hosted runners logs in to GHCR through its own copy of the Docker client config
    ($RUNNER_TEMP/docker-config, via DOCKER_CONFIG; #1776). actions/attest-build-provenance with push-to-registry
    reads registry credentials from $HOME/.docker/config.json only and ignores DOCKER_CONFIG, so it found no
    credentials and failed the v0.16.0 release. The attest step must therefore see a HOME whose .docker is the job's
    own config.

    The air-gapped bundle's checksums named every file under the build runner's folders, so the documented
    sha256sum -c failed on every line, and nothing covered the archive itself (#1942). The release must check both
    as a customer would before publishing them.
#>

BeforeAll {
    $script:Workflow = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows' 'release.yml') -Raw) -replace "`r", ''

    # The text of one job, from its key to the next job's.
    function Get-WorkflowJob {
        param([string]$Name)
        [regex]::Match($script:Workflow, "(?ms)^  ${Name}:\s*$.*?(?=^  [a-z0-9-]+:\s*$|\z)").Value
    }
}

Describe 'the provenance attestation in release.yml' {

    It 'runs with a HOME whose .docker is the job''s own Docker config, since the action ignores DOCKER_CONFIG' {
        $attestStep = [regex]::Match($script:Workflow, '(?ms)^      - name: Attest build provenance\s*$.*?(?=^      - name:)').Value
        $attestStep | Should -Match 'push-to-registry: true'
        $attestStep | Should -Match '(?m)^\s+HOME: \$\{\{ runner\.temp \}\}/attest-home\s*$'

        $beforeAttest = $script:Workflow.Substring(0, $script:Workflow.IndexOf('      - name: Attest build provenance'))
        $beforeAttest | Should -Match 'ln -sfn "\$DOCKER_CONFIG" "\$RUNNER_TEMP/attest-home/\.docker"'
    }
}

Describe 'the air-gapped bundle in release.yml' {

    It 'checks the archive and every file in it against their checksums, as a customer would, before uploading it' {
        $job = Get-WorkflowJob 'create-bundle'
        $check = $job.IndexOf('./scripts/Test-ReleaseBundle.ps1 -ArchivePath ./release-output/jim-release-$version.tar.gz')
        $check | Should -BeGreaterThan -1
        $check | Should -BeLessThan $job.IndexOf('- name: Upload bundle artifact')
    }

    It 'publishes the archive''s checksum with the release' {
        Get-WorkflowJob 'create-bundle' | Should -Match '(?m)^\s+\./release-output/jim-release-\$\{\{ steps\.version\.outputs\.VERSION \}\}\.tar\.gz\.sha256\s*$'
        Get-WorkflowJob 'create-release' | Should -Match '(?m)^\s+\./artifacts/jim-release-\$\{VERSION\}\.tar\.gz\.sha256 \\$'
    }

    It 'publishes the PowerShell module without its development-only files' {
        Get-WorkflowJob 'publish-powershell' | Should -Match "-Exclude 'Tests', 'CLAUDE\.md'"
    }
}

Describe 'publishing the screenshots in release.yml' {

    # v0.17.0's step ran the script with pwsh -File, which hands each argument over as a string, so the two sites never
    # became a list and the script stopped before merging either pull request (#582). The step's own run block is run
    # here as Actions runs it, against a stand-in script that records what it was given.
    It 'hands the merge script both sites and the release tag' {
        $step = [regex]::Match((Get-WorkflowJob 'publish-screenshots'),
            '(?ms)^      - name: Merge the screenshot pull requests\s*$.*?^        run: \|\s*$\n(?<run>(?:^          .*\n?)+)').Groups['run'].Value
        $step | Should -Not -BeNullOrEmpty
        $run = ($step -split "`n" | ForEach-Object { if ($_.Length -ge 10) { $_.Substring(10) } else { $_ } }) -join "`n"

        $record = Join-Path $TestDrive 'received.json'
        $stub = Join-Path $TestDrive 'Merge-ReleaseScreenshots.ps1'
        Set-Content -LiteralPath $stub -Value @"
param([string]`$Version, [string[]]`$Repository)
@{ Version = `$Version; Repository = `$Repository } | ConvertTo-Json | Set-Content -LiteralPath '$record'
"@
        $script = Join-Path $TestDrive 'step.ps1'
        Set-Content -LiteralPath $script -Value ($run.Replace('./scripts/screenshots/Merge-ReleaseScreenshots.ps1', $stub))

        $env:VERSION_TAG = 'v0.17.0'; $env:OWNER = 'TetronIO'
        try { & pwsh -NoProfile -NonInteractive -Command ". '$script'" 2>&1 | Out-Null }
        finally { Remove-Item Env:VERSION_TAG, Env:OWNER }

        $LASTEXITCODE | Should -Be 0
        $received = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
        $received.Version | Should -Be 'v0.17.0'
        @($received.Repository) | Should -Be @('TetronIO/junctional.io', 'TetronIO/tetron.io')
    }
}
