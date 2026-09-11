# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Filter-Sarif.ps1.

.DESCRIPTION
    The filter is the enforcement point for the CodeQL configuration's paths-ignore
    block on compiled languages (see the script's own header). Two things are
    exercised here:

      1. A result whose primary location is inside an excluded path is removed
         (the original behaviour, proven on PR #1177).

      2. A result whose primary location is in shipped code but whose taint
         SOURCE is inside an excluded path is also removed. Unit tests read
         fixture hostnames from environment variables and pass them into the
         LDAP Connector; CodeQL treats an environment variable read as sensitive,
         follows it into the connector's log calls, and reports "clear-text
         storage" at the log call. The alert is placed in src/, so the primary
         location filter never sees it, but the only thing that makes it an alert
         is the excluded test file at the other end of the flow.

    A result is removed on the second rule only when EVERY code flow it carries
    starts in an excluded path: one flow from shipped code is a real finding,
    however many test-sourced flows sit beside it.

    All fixtures are written under $TestDrive; nothing here reads the live
    repository's SARIF or configuration.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Filter-Sarif.ps1')).Path

    # A location object in the shape CodeQL writes into SARIF.
    function New-SarifLocation([string]$Uri, [int]$Line = 1) {
        return @{
            physicalLocation = @{
                artifactLocation = @{ uri = $Uri; uriBaseId = '%SRCROOT%' }
                region           = @{ startLine = $Line }
            }
        }
    }

    # A code flow whose steps run through the given URIs in order; the first is the source.
    function New-SarifCodeFlow([string[]]$StepUris) {
        $steps = foreach ($uri in $StepUris) { @{ location = (New-SarifLocation $uri) } }
        return @{ threadFlows = @(@{ locations = @($steps) }) }
    }

    # A result reported at $PrimaryUri, optionally carrying code flows.
    function New-SarifResult([string]$RuleId, [string]$PrimaryUri, [object[]]$CodeFlows = $null) {
        $result = @{
            ruleId    = $RuleId
            message   = @{ text = "finding at $PrimaryUri" }
            locations = @((New-SarifLocation $PrimaryUri))
        }
        if ($null -ne $CodeFlows) { $result.codeFlows = @($CodeFlows) }
        return $result
    }

    # Writes a configuration file and a single-run SARIF file holding $Results into a fresh
    # directory, runs the filter over it, and returns the results that survived together with
    # the script's console output.
    function Invoke-Filter([object[]]$Results, [string[]]$PathsIgnore = @('test/JIM.*.Tests/**')) {
        $root = Join-Path $TestDrive ("Filter_" + [guid]::NewGuid().ToString('N'))
        $sarifDir = Join-Path $root 'sarif-results'
        New-Item -ItemType Directory -Path $sarifDir -Force | Out-Null

        $configLines = @('paths-ignore:') + @($PathsIgnore | ForEach-Object { "  - $_" })
        $configPath = Join-Path $root 'codeql-config.yml'
        Set-Content -Path $configPath -Value $configLines

        $sarif = @{
            version = '2.1.0'
            runs    = @(@{
                tool    = @{ driver = @{ name = 'CodeQL' } }
                results = @($Results)
            })
        }
        $sarifPath = Join-Path $sarifDir 'csharp.sarif'
        $sarif | ConvertTo-Json -Depth 100 | Set-Content -Path $sarifPath

        $output = & $script:ScriptPath -SarifDirectory $sarifDir -ConfigFile $configPath 6>&1 | Out-String
        $filtered = Get-Content $sarifPath -Raw | ConvertFrom-Json -Depth 100

        return [pscustomobject]@{
            Results = @($filtered.runs[0].results)
            Output  = $output
        }
    }
}

Describe 'Filter-Sarif.ps1 primary location exclusion' {
    It 'removes a result reported inside an excluded path' {
        $outcome = Invoke-Filter @(
            (New-SarifResult 'cs/web/xss' 'test/JIM.Worker.Tests/SomeTests.cs')
        )
        $outcome.Results.Count | Should -Be 0
    }

    It 'keeps a result reported in shipped code with no code flows' {
        $outcome = Invoke-Filter @(
            (New-SarifResult 'cs/web/xss' 'src/JIM.Web/Controllers/Api/SomeController.cs')
        )
        $outcome.Results.Count | Should -Be 1
    }

    It 'keeps a result that has no primary location at all' {
        $result = New-SarifResult 'cs/web/xss' 'src/JIM.Web/X.cs'
        $result.Remove('locations')
        $outcome = Invoke-Filter @($result)
        $outcome.Results.Count | Should -Be 1
    }
}

Describe 'Filter-Sarif.ps1 taint source exclusion' {
    It 'removes a result whose only code flow starts in an excluded path' {
        $flow = New-SarifCodeFlow @('test/JIM.Worker.Tests/Connectors/LdapsCertificateValidationTests.cs', 'src/JIM.Connectors/LDAP/LdapConnector.cs')
        $outcome = Invoke-Filter @(
            (New-SarifResult 'cs/cleartext-storage-of-sensitive-information' 'src/JIM.Connectors/LDAP/LdapConnector.cs' @($flow))
        )
        $outcome.Results.Count | Should -Be 0
    }

    It 'removes a result when every one of its code flows starts in an excluded path' {
        $flows = @(
            (New-SarifCodeFlow @('test/JIM.Worker.Tests/A.cs', 'src/JIM.Connectors/LDAP/LdapConnector.cs')),
            (New-SarifCodeFlow @('test/JIM.Web.Api.Tests/B.cs', 'src/JIM.Connectors/LDAP/LdapConnectorUtilities.cs', 'src/JIM.Connectors/LDAP/LdapConnector.cs'))
        )
        $outcome = Invoke-Filter @(
            (New-SarifResult 'cs/cleartext-storage-of-sensitive-information' 'src/JIM.Connectors/LDAP/LdapConnector.cs' $flows)
        )
        $outcome.Results.Count | Should -Be 0
    }

    It 'keeps a result when any code flow starts in shipped code' {
        $flows = @(
            (New-SarifCodeFlow @('test/JIM.Worker.Tests/A.cs', 'src/JIM.Connectors/LDAP/LdapConnector.cs')),
            (New-SarifCodeFlow @('src/JIM.Web/Controllers/Api/SomeController.cs', 'src/JIM.Connectors/LDAP/LdapConnector.cs'))
        )
        $outcome = Invoke-Filter @(
            (New-SarifResult 'cs/cleartext-storage-of-sensitive-information' 'src/JIM.Connectors/LDAP/LdapConnector.cs' $flows)
        )
        $outcome.Results.Count | Should -Be 1
    }

    It 'keeps a result whose flow merely passes through an excluded path after a shipped-code source' {
        $flow = New-SarifCodeFlow @('src/JIM.Web/A.cs', 'test/JIM.Worker.Tests/Helper.cs', 'src/JIM.Connectors/LDAP/LdapConnector.cs')
        $outcome = Invoke-Filter @(
            (New-SarifResult 'cs/cleartext-storage-of-sensitive-information' 'src/JIM.Connectors/LDAP/LdapConnector.cs' @($flow))
        )
        $outcome.Results.Count | Should -Be 1
    }

    It 'names the excluded source in the log so the removal is never silent' {
        $flow = New-SarifCodeFlow @('test/JIM.Worker.Tests/Connectors/LdapsCertificateValidationTests.cs', 'src/JIM.Connectors/LDAP/LdapConnector.cs')
        $outcome = Invoke-Filter @(
            (New-SarifResult 'cs/cleartext-storage-of-sensitive-information' 'src/JIM.Connectors/LDAP/LdapConnector.cs' @($flow))
        )
        $outcome.Output | Should -Match 'cs/cleartext-storage-of-sensitive-information'
        $outcome.Output | Should -Match 'test/JIM\.Worker\.Tests/Connectors/LdapsCertificateValidationTests\.cs'
    }

    It 'leaves a mixed set with exactly the shipped-code findings' {
        $testSourced = New-SarifCodeFlow @('test/JIM.Worker.Tests/A.cs', 'src/JIM.Connectors/LDAP/LdapConnector.cs')
        $srcSourced  = New-SarifCodeFlow @('src/JIM.Web/A.cs', 'src/JIM.Web/B.cs')
        $outcome = Invoke-Filter @(
            (New-SarifResult 'cs/cleartext-storage-of-sensitive-information' 'src/JIM.Connectors/LDAP/LdapConnector.cs' @($testSourced)),
            (New-SarifResult 'cs/user-controlled-bypass' 'src/JIM.Web/B.cs' @($srcSourced)),
            (New-SarifResult 'cs/web/xss' 'test/JIM.Web.Tests/Host.cs'),
            (New-SarifResult 'cs/web/xss' 'src/JIM.Web/C.cs')
        )
        @($outcome.Results | ForEach-Object { $_.ruleId }) | Should -Be @('cs/user-controlled-bypass', 'cs/web/xss')
        @($outcome.Results | ForEach-Object { $_.locations[0].physicalLocation.artifactLocation.uri }) | Should -Be @('src/JIM.Web/B.cs', 'src/JIM.Web/C.cs')
    }
}
