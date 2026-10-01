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
#>

BeforeAll {
    $script:Workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows' 'release.yml') -Raw
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
