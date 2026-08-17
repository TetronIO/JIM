# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for .github/scripts/check-tooling-pins.ps1.

.DESCRIPTION
    The apt pin check learnt from #1374 that asking "is a NEWER version
    available?" leaves the more serious question unasked: is the version we
    already pin still obtainable? A withdrawn pin means the environment can no
    longer be built, and nothing else notices, because the drift comes from the
    registry rather than from any commit. npm unpublishes and NuGet hard-deletes
    are rarer than an Ubuntu archive rotation, but the failure mode is identical
    and the check costs nothing: both registries already return the full version
    list the script queries for "latest".

    The registry is faked by shadowing Invoke-RestMethod with a function, which
    beats a cmdlet in PowerShell's command resolution and is inherited by the
    scope the script under test runs in. State lives in $global: because a
    function's $script: scope resolves against the script running it, not the one
    that defined it.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'check-tooling-pins.ps1')).Path

    # The two registry documents the script reads, in the shape each registry
    # actually returns: npm a packument keyed by version, NuGet a flat-container
    # index listing versions ascending.
    function New-FakeRegistry {
        param(
            [string]$NpmLatest = '0.0.80',
            [string[]]$NpmVersions = @('0.0.78', '0.0.79', '0.0.80'),
            [string[]]$NuGetVersions = @('10.0.9', '10.0.10', '10.0.11')
        )

        $global:FakeRegistry = @{
            NpmLatest     = $NpmLatest
            NpmVersions   = $NpmVersions
            NuGetVersions = $NuGetVersions
            Failing       = $false
            Uris          = [System.Collections.ArrayList]::new()
        }
    }

    function global:Invoke-RestMethod {
        param([string]$Uri, [int]$TimeoutSec)

        $global:FakeRegistry.Uris.Add($Uri) | Out-Null
        if ($global:FakeRegistry.Failing) { throw "the fake registry is refusing connections" }

        if ($Uri -match 'registry\.npmjs\.org') {
            $versions = [ordered]@{}
            foreach ($v in $global:FakeRegistry.NpmVersions) { $versions[$v] = @{ name = '@playwright/mcp'; version = $v } }
            return [pscustomobject]@{
                'dist-tags' = [pscustomobject]@{ latest = $global:FakeRegistry.NpmLatest }
                versions    = [pscustomobject]$versions
            }
        }

        if ($Uri -match 'api\.nuget\.org') {
            return [pscustomobject]@{ versions = @($global:FakeRegistry.NuGetVersions) }
        }

        throw "the fake registry does not know $Uri"
    }

    # A working tree holding the real pin files, so the script's own regexes are
    # what is under test rather than a simplified stand-in.
    function New-PinTree {
        param([string]$PlaywrightVersion = '0.0.79', [string]$DotnetEfVersion = '10.0.10')

        $root = Join-Path $TestDrive ("Pins_" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $root '.devcontainer') -Force | Out-Null

        Set-Content -Path (Join-Path $root '.devcontainer/setup.sh') -Value @"
#!/usr/bin/env bash
if dotnet tool install --global dotnet-ef --version $DotnetEfVersion; then
    print_success "dotnet-ef $DotnetEfVersion installed globally"
fi
PLAYWRIGHT_MCP_VERSION="$PlaywrightVersion"
npm install -g "@playwright/mcp@`${PLAYWRIGHT_MCP_VERSION}" --silent
"@

        Set-Content -Path (Join-Path $root '.mcp.json') -Value @"
{
  "mcpServers": {
    "playwright": {
      "command": "npx",
      "args": ["-y", "@playwright/mcp@$PlaywrightVersion"]
    }
  }
}
"@

        return $root
    }

    function Invoke-CheckToolingPins {
        param([string]$Tree, [switch]$Apply, [string]$OutputFile)

        Push-Location $Tree
        try {
            $env:GITHUB_OUTPUT = $OutputFile
            $script:Output = & $script:ScriptPath -Apply:$Apply *>&1 | Out-String
            $script:ExitCode = $LASTEXITCODE
        } finally {
            $env:GITHUB_OUTPUT = $null
            Pop-Location
        }
    }
}

Describe 'check-tooling-pins.ps1' {

    BeforeEach {
        New-FakeRegistry
    }

    Context 'when every pin is current and still published' {

        It 'reports nothing to evaluate' {
            New-FakeRegistry -NpmLatest '0.0.79' -NpmVersions @('0.0.78', '0.0.79') -NuGetVersions @('10.0.9', '10.0.10')

            Invoke-CheckToolingPins -Tree (New-PinTree)

            $script:ExitCode | Should -Be 0
        }
    }

    Context 'when a newer version is available' {

        It 'proposes the bump' {
            $tree = New-PinTree
            Invoke-CheckToolingPins -Tree $tree

            $script:ExitCode | Should -Be 2
            Get-Content (Join-Path $tree 'tooling-pin-pr-body.md') -Raw | Should -Match '0\.0\.80'
        }
    }

    Context 'when the pinned version has been withdrawn from the registry' {

        It 'fails loudly rather than reporting the pin as current' {
            # 0.0.79 is pinned, unpublished upstream, and 0.0.78 is the latest
            # left: nothing is "behind", so the newer-version check alone sees a
            # perfectly healthy pin.
            New-FakeRegistry -NpmLatest '0.0.78' -NpmVersions @('0.0.77', '0.0.78') -NuGetVersions @('10.0.9', '10.0.10')

            Invoke-CheckToolingPins -Tree (New-PinTree)

            $script:ExitCode | Should -Be 3
            $script:Output | Should -Match 'WITHDRAWN'
            $script:Output | Should -Match '@playwright/mcp'
        }

        It 'catches a withdrawn NuGet pin as well as an npm one' {
            New-FakeRegistry -NuGetVersions @('10.0.9', '10.0.11')

            Invoke-CheckToolingPins -Tree (New-PinTree)

            $script:ExitCode | Should -Be 3
            $script:Output | Should -Match 'dotnet-ef'
        }

        It 'still proposes and applies the bump that clears it' {
            # Reporting the problem must not suppress the fix: the pinned 0.0.79
            # is gone AND 0.0.80 is available, so the run has to do both.
            New-FakeRegistry -NpmLatest '0.0.80' -NpmVersions @('0.0.78', '0.0.80') -NuGetVersions @('10.0.9', '10.0.10')
            $tree = New-PinTree

            Invoke-CheckToolingPins -Tree $tree -Apply

            $script:ExitCode | Should -Be 3
            Get-Content (Join-Path $tree 'tooling-pin-pr-body.md') -Raw | Should -Match '0\.0\.80'
            Get-Content (Join-Path $tree '.mcp.json') -Raw | Should -Match '@playwright/mcp@0\.0\.80'
            Get-Content (Join-Path $tree '.devcontainer/setup.sh') -Raw | Should -Match 'PLAYWRIGHT_MCP_VERSION="0\.0\.80"'
        }

        It 'tells the workflow, so the run can be failed after the PR is raised' {
            New-FakeRegistry -NpmLatest '0.0.78' -NpmVersions @('0.0.77', '0.0.78') -NuGetVersions @('10.0.9', '10.0.10')
            $outputFile = Join-Path $TestDrive ("out_" + [guid]::NewGuid().ToString('N') + '.txt')

            Invoke-CheckToolingPins -Tree (New-PinTree) -OutputFile $outputFile

            $written = Get-Content $outputFile -Raw
            $written | Should -Match 'unavailable=true'
            $written | Should -Match 'unavailable_tools=.*playwright'
        }
    }

    Context 'when a registry cannot be reached' {

        It 'refuses to report a result' {
            New-FakeRegistry
            $global:FakeRegistry.Failing = $true

            Invoke-CheckToolingPins -Tree (New-PinTree)

            # A silent false negative would leave the pin unmonitored while
            # looking healthy, which is the failure mode this whole family of
            # checks exists to avoid.
            $script:ExitCode | Should -Be 1
        }
    }
}
