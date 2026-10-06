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
