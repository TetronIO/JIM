# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for .github/scripts/Invoke-TrivyImageScan.ps1.

.DESCRIPTION
    CI's scan-images job and the release workflow both scan JIM's images before anything is published, and until
    #2075 they did so with different scanners (CI a pinned image, the release whatever version trivy-action brought)
    and different gates (CI the CVSS score, the release Trivy's own severity label). A release could therefore fail
    on a finding CI had passed, or ship one CI would have blocked. Both now run this script.

    Docker is faked by shadowing the native command with a function, which beats an external command in PowerShell's
    command resolution and is inherited by the scope the script under test runs in. State lives in $global: for the
    reason given in CheckToolingPins.Tests.ps1.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Invoke-TrivyImageScan.ps1')).Path
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
    $script:PinnedImage = 'aquasec/trivy:0.76.0@sha256:' + ('a' * 64)

    # A SARIF document in the shape Trivy writes: CVSS scores on the rules catalogue, findings referring to them.
    function New-Sarif {
        param([hashtable]$Scores, [string[]]$Findings)
        $rules = @($Scores.GetEnumerator() | ForEach-Object { @{ id = $_.Key; properties = @{ 'security-severity' = $_.Value } } })
        $results = @($Findings | ForEach-Object { @{ ruleId = $_ } })
        return (@{ runs = @(@{ tool = @{ driver = @{ rules = $rules } }; results = $results }) } | ConvertTo-Json -Depth 10)
    }

    function New-WorkTree {
        param([string]$Pin = $script:PinnedImage)
        $root = Join-Path $TestDrive ("Scan_" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $root '.github') -Force | Out-Null
        Set-Content -Path (Join-Path $root '.github/trivy-image.txt') -Value "# The scanner.`n$Pin"
        return $root
    }

    # The fake Docker: records every call, fails the first $DownloadFailures DB downloads, and has the scan write
    # the SARIF document Trivy would have written.
    function New-FakeDocker {
        param([int]$DownloadFailures = 0, [string]$Sarif = (New-Sarif -Scores @{} -Findings @()))
        $global:FakeDocker = @{ Calls = [System.Collections.ArrayList]::new(); DownloadFailures = $DownloadFailures; Sarif = $Sarif }
    }

    function global:docker {
        $global:FakeDocker.Calls.Add(($args -join ' ')) | Out-Null
        if ($args -contains '--download-db-only') {
            if ($global:FakeDocker.DownloadFailures -gt 0) {
                $global:FakeDocker.DownloadFailures--
                $global:LASTEXITCODE = 1
                return
            }
        } elseif ($args -contains '--format') {
            $output = $args[[array]::IndexOf($args, '--output') + 1]
            Set-Content -Path (Join-Path (Get-Location) $output) -Value $global:FakeDocker.Sarif
        }
        $global:LASTEXITCODE = 0
    }

    function global:Start-Sleep { param([int]$Seconds) }

    function Invoke-Scan {
        param([string]$Tree, [string[]]$Arguments = @('-ImageRef', 'jim-web:scan'))
        Push-Location $Tree
        try {
            $script:Output = & $script:ScriptPath @Arguments *>&1 | Out-String
            $script:ExitCode = $LASTEXITCODE
        } finally {
            Pop-Location
        }
    }
}

AfterAll {
    Remove-Item -Path Function:docker -ErrorAction SilentlyContinue
    Remove-Item -Path Function:Start-Sleep -ErrorAction SilentlyContinue
    Remove-Variable -Name FakeDocker -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Invoke-TrivyImageScan.ps1' {

    BeforeEach {
        New-FakeDocker
    }

    Context 'the scanner it runs' {

        It 'runs the image pinned in .github/trivy-image.txt' {
            Invoke-Scan -Tree (New-WorkTree)

            $script:ExitCode | Should -Be 0
            @($global:FakeDocker.Calls | Where-Object { $_ -match [regex]::Escape($script:PinnedImage) }).Count | Should -Be 2
        }

        It 'refuses a scanner that is not pinned by digest, before running anything' {
            Invoke-Scan -Tree (New-WorkTree -Pin 'aquasec/trivy:latest')

            $script:ExitCode | Should -Be 1
            $script:Output | Should -Match 'digest'
            $global:FakeDocker.Calls.Count | Should -Be 0
        }

        It 'retries the vulnerability database download' {
            New-FakeDocker -DownloadFailures 2

            Invoke-Scan -Tree (New-WorkTree)

            $script:ExitCode | Should -Be 0
            @($global:FakeDocker.Calls | Where-Object { $_ -match '--download-db-only' }).Count | Should -Be 3
        }

        It 'fails when the vulnerability database cannot be downloaded' {
            New-FakeDocker -DownloadFailures 3

            Invoke-Scan -Tree (New-WorkTree)

            $script:ExitCode | Should -Be 1
            @($global:FakeDocker.Calls | Where-Object { $_ -match '--format' }).Count | Should -Be 0
        }

        It 'applies .trivyignore and leaves out findings with no fix' {
            Invoke-Scan -Tree (New-WorkTree)

            $scan = @($global:FakeDocker.Calls | Where-Object { $_ -match '--format sarif' })[0]
            $scan | Should -Match '--ignorefile \.trivyignore'
            $scan | Should -Match 'TRIVY_IGNORE_UNFIXED=true'
        }
    }

    Context 'the gate' {

        It 'fails on a finding scored HIGH (CVSS 7.0 or more) and names it' {
            New-FakeDocker -Sarif (New-Sarif -Scores @{ 'CVE-2026-0001' = '7.5'; 'CVE-2026-0002' = '5.0' } -Findings @('CVE-2026-0001', 'CVE-2026-0002'))

            Invoke-Scan -Tree (New-WorkTree)

            $script:ExitCode | Should -Be 1
            $script:Output | Should -Match 'CVE-2026-0001 \(CVSS 7\.5\)'
            $script:Output | Should -Not -Match 'CVE-2026-0002 \(CVSS'
        }

        It 'passes when every finding scores below HIGH' {
            New-FakeDocker -Sarif (New-Sarif -Scores @{ 'CVE-2026-0003' = '6.9'; 'CVE-2026-0004' = '2.0' } -Findings @('CVE-2026-0003', 'CVE-2026-0004'))

            Invoke-Scan -Tree (New-WorkTree)

            $script:ExitCode | Should -Be 0
            $script:Output | Should -Match 'MEDIUM:\s+1'
            $script:Output | Should -Match 'LOW:\s+1'
        }

        It 'fails when the scan wrote no SARIF, rather than reporting a clean image' {
            $global:FakeDocker.Sarif = $null
            function global:docker { $global:LASTEXITCODE = 0 }
            try {
                Invoke-Scan -Tree (New-WorkTree)
            } finally {
                Remove-Item -Path Function:docker
            }

            $script:ExitCode | Should -Be 1
            $script:Output | Should -Match 'SARIF'
        }
    }

    Context "the repository's workflows" {

        It 'are the only callers, so CI and the release scan with one scanner and one gate' {
            # Dependabot does not read an image named in a workflow's env, and the App that commits the pin check's
            # bumps cannot write .github/workflows/, so a scanner pinned in a workflow, or brought by trivy-action,
            # goes unwatched again (#2075).
            $workflows = Get-ChildItem -Path (Join-Path $script:RepoRoot '.github/workflows') -Filter '*.yml'
            foreach ($workflow in $workflows) {
                $text = Get-Content $workflow.FullName -Raw
                $text | Should -Not -Match 'aquasec/trivy:\d[\w.\-]*@sha256:' -Because "$($workflow.Name) must not pin its own scanner"
                $text | Should -Not -Match 'uses:\s*aquasecurity/trivy-action' -Because "$($workflow.Name) must not bring its own scanner"
            }
            foreach ($name in @('ci.yml', 'release.yml')) {
                Get-Content (Join-Path $script:RepoRoot ".github/workflows/$name") -Raw | Should -Match 'Invoke-TrivyImageScan\.ps1'
            }
        }
    }
}
