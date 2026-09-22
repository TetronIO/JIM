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
    function New-SarifResult([string]$RuleId, [string]$PrimaryUri, [object[]]$CodeFlows = $null, [int]$Line = 1) {
        $result = @{
            ruleId    = $RuleId
            message   = @{ text = "finding at $PrimaryUri" }
            locations = @((New-SarifLocation $PrimaryUri $Line))
        }
        if ($null -ne $CodeFlows) { $result.codeFlows = @($CodeFlows) }
        return $result
    }

    # Writes a configuration file and a single-run SARIF file holding $Results into a fresh
    # directory, runs the filter over it, and returns the results that survived together with
    # the script's console output.
    function Invoke-Filter([object[]]$Results, [string[]]$PathsIgnore = @('test/JIM.*.Tests/**'), [hashtable]$SourceFiles = @{}) {
        $root = Join-Path $TestDrive ("Filter_" + [guid]::NewGuid().ToString('N'))
        $sarifDir = Join-Path $root 'sarif-results'
        New-Item -ItemType Directory -Path $sarifDir -Force | Out-Null

        # Source files the suppression rule reads, keyed by the repository-relative URI a result
        # names, each value being the file's lines. The root doubles as the source root, so a URI
        # with no entry here is a file the filter cannot find.
        foreach ($uri in $SourceFiles.Keys) {
            $path = Join-Path $root $uri
            New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
            Set-Content -Path $path -Value $SourceFiles[$uri]
        }

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

        $output = & $script:ScriptPath -SarifDirectory $sarifDir -ConfigFile $configPath -SourceRoot $root 6>&1 | Out-String
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

Describe 'Filter-Sarif.ps1 in-code suppression' {
    BeforeAll {
        $script:Rule = 'cs/cleartext-storage-of-sensitive-information'
        $script:Uri = 'src/JIM.Web/Controllers/Api/HistoryController.cs'

        # A file whose line 3 is flagged, with whatever sits on line 2 supplied by the test.
        function New-SourceLines([string]$LineAbove) {
            return @(
                '        var result = await Cleanup();',
                $LineAbove,
                '        _logger.LogInformation("Deleted {Count}", result.PasswordRecordsDeleted);',
                '        return Ok(result);'
            )
        }
    }

    It 'removes a result whose flagged line is preceded by a codeql[] comment naming its rule with a rationale' {
        $outcome = Invoke-Filter @((New-SarifResult $script:Rule $script:Uri $null 3)) -SourceFiles @{
            $script:Uri = New-SourceLines "        // codeql[$($script:Rule)] counts of deleted rows, not credentials"
        }
        $outcome.Results.Count | Should -Be 0
    }

    It 'removes every result that shares the suppressed line' {
        $results = @(
            (New-SarifResult $script:Rule $script:Uri $null 3),
            (New-SarifResult $script:Rule $script:Uri $null 3),
            (New-SarifResult $script:Rule $script:Uri $null 3)
        )
        $outcome = Invoke-Filter $results -SourceFiles @{
            $script:Uri = New-SourceLines "        // codeql[$($script:Rule)] counts of deleted rows, not credentials"
        }
        $outcome.Results.Count | Should -Be 0
    }

    It 'honours a comment that lists several rules' {
        $outcome = Invoke-Filter @((New-SarifResult $script:Rule $script:Uri $null 3)) -SourceFiles @{
            $script:Uri = New-SourceLines "        // codeql[cs/log-forging, $($script:Rule)] counts, already sanitised"
        }
        $outcome.Results.Count | Should -Be 0
    }

    It 'honours a hash-comment line, so workflow and PowerShell findings can be suppressed the same way' {
        $uri = '.github/workflows/ci.yml'
        $outcome = Invoke-Filter @((New-SarifResult 'actions/missing-workflow-permissions' $uri $null 3)) -SourceFiles @{
            $uri = @('jobs:', '  # codeql[actions/missing-workflow-permissions] permissions are set at the workflow level', '  build:', '    runs-on: ubuntu-latest')
        }
        $outcome.Results.Count | Should -Be 0
    }

    It 'keeps a result when the comment names a different rule' {
        $outcome = Invoke-Filter @((New-SarifResult $script:Rule $script:Uri $null 3)) -SourceFiles @{
            $script:Uri = New-SourceLines '        // codeql[cs/log-forging] the count is an integer'
        }
        $outcome.Results.Count | Should -Be 1
    }

    It 'keeps a result when the comment is not on the line immediately above the flagged line' {
        $outcome = Invoke-Filter @((New-SarifResult $script:Rule $script:Uri $null 4)) -SourceFiles @{
            $script:Uri = New-SourceLines "        // codeql[$($script:Rule)] counts of deleted rows, not credentials"
        }
        $outcome.Results.Count | Should -Be 1
    }

    It 'does not honour a trailing comment on the flagged line itself, matching CodeQL' {
        $outcome = Invoke-Filter @((New-SarifResult $script:Rule $script:Uri $null 3)) -SourceFiles @{
            $script:Uri = @(
                '        var result = await Cleanup();',
                '',
                "        _logger.LogInformation(`"Deleted {Count}`", result.PasswordRecordsDeleted); // codeql[$($script:Rule)] counts",
                '        return Ok(result);'
            )
        }
        $outcome.Results.Count | Should -Be 1
    }

    It 'keeps a result, and says why, when the comment carries no rationale' {
        $outcome = Invoke-Filter @((New-SarifResult $script:Rule $script:Uri $null 3)) -SourceFiles @{
            $script:Uri = New-SourceLines "        // codeql[$($script:Rule)]"
        }
        $outcome.Results.Count | Should -Be 1
        $outcome.Output | Should -Match 'no rationale'
        $outcome.Output | Should -Match ([regex]::Escape("$($script:Uri):2"))
    }

    It 'keeps a result whose source file cannot be found under the source root' {
        $outcome = Invoke-Filter @((New-SarifResult $script:Rule $script:Uri $null 3))
        $outcome.Results.Count | Should -Be 1
    }

    It 'keeps a result that has no line number to look above' {
        $result = New-SarifResult $script:Rule $script:Uri
        $result.locations[0].physicalLocation.Remove('region')
        $outcome = Invoke-Filter @($result) -SourceFiles @{
            $script:Uri = New-SourceLines "        // codeql[$($script:Rule)] counts of deleted rows, not credentials"
        }
        $outcome.Results.Count | Should -Be 1
    }

    It 'logs each suppression with the rule, the location and the rationale so it is never silent' {
        $outcome = Invoke-Filter @((New-SarifResult $script:Rule $script:Uri $null 3)) -SourceFiles @{
            $script:Uri = New-SourceLines "        // codeql[$($script:Rule)] counts of deleted rows, not credentials"
        }
        $outcome.Output | Should -Match 'Suppressed'
        $outcome.Output | Should -Match ([regex]::Escape("$($script:Rule) at $($script:Uri):3"))
        $outcome.Output | Should -Match 'counts of deleted rows, not credentials'
    }

    It 'counts suppressions separately from path exclusions in the summary line' {
        $results = @(
            (New-SarifResult $script:Rule $script:Uri $null 3),
            (New-SarifResult 'cs/web/xss' 'test/JIM.Web.Tests/Host.cs'),
            (New-SarifResult 'cs/web/xss' 'src/JIM.Web/C.cs')
        )
        $outcome = Invoke-Filter $results -SourceFiles @{
            $script:Uri = New-SourceLines "        // codeql[$($script:Rule)] counts of deleted rows, not credentials"
        }
        $outcome.Results.Count | Should -Be 1
        $outcome.Output | Should -Match 'kept 1 result\(s\), removed 1 .*, suppressed 1 '
    }
}
