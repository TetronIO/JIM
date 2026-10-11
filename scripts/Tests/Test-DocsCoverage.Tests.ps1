# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Test-DocsCoverage.ps1.

.DESCRIPTION
    Exercises each coverage check of the documentation standard
    (engineering/DOCUMENTATION_STANDARD.md, #1454) against a fixture
    repository: exported cmdlets (COV-1), environment variables (COV-2),
    expression functions (COV-6) and links from the product into the docs
    (LNK-3), each checked both ways, and the reasoned allow-list.

    Every fixture is a small repository under $TestDrive with the same layout
    as the real one, so none of this depends on the live sources or pages.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Test-DocsCoverage.ps1')).Path

    function ConvertTo-Psd1 {
        param($Value, [int]$Indent = 0)
        $pad = ' ' * $Indent
        if ($Value -is [hashtable]) {
            $lines = foreach ($key in ($Value.Keys | Sort-Object)) {
                "$pad    '$key' = $(ConvertTo-Psd1 -Value $Value[$key] -Indent ($Indent + 4))"
            }
            return "@{`n$($lines -join "`n")`n$pad}"
        }
        if ($Value -is [array]) {
            $items = foreach ($item in $Value) { ConvertTo-Psd1 -Value $item -Indent $Indent }
            return "@($($items -join ', '))"
        }
        return "'$($Value -replace "'", "''")'"
    }

    function Get-Page {
        param([string]$Title, [string]$Body)
        "---`ntitle: `"$Title`"`n---`n`n# $Title`n`n$Body"
    }

    # A repository in which everything is documented; tests change one thing at a time.
    function Get-DefaultFiles {
        @{
            'src/JIM.PowerShell/JIM.psd1'                              = "@{`n    FunctionsToExport = @(`n        'Get-JIMThing',`n        'Set-JIMThing'`n    )`n}`n"
            'src/JIM.Models/Core/Constants.cs'                         = "public static class Constants`n{`n    public static class Config`n    {`n        public static string LogLevel => `"JIM_LOG_LEVEL`";`n    }`n}`n"
            'src/JIM.Worker/Worker.cs'                                 = "var x = Environment.GetEnvironmentVariable(`"JIM_CACHE_SIZE`");`ninternal const string AliasPrefix = `"JIM_RELATED`";`n"
            'src/JIM.PowerShell/Public/Get-JIMThing.ps1'               = "<#`n.EXAMPLE`n    Connect-JIM -ApiKey `$env:JIM_API_KEY`n#>`nfunction Get-JIMThing { }`n"
            'src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs' = "target.SetFunction(`"Trim`", x);`ntarget.SetFunction(`"Upper`", x);`n"
            'docker-compose.yml'                                       = "services:`n  web:`n    environment:`n      - JIM_WEB_PORT=`${JIM_WEB_PORT:-443}`n      - POSTGRES_DB=`$`${POSTGRES_DB}`n"
            'deploy/docker-compose.production.yml'                     = "services: {}`n"
            '.env.example'                                             = "JIM_LOG_LEVEL=Information`n"
            'deploy/setup.sh'                                          = "#!/usr/bin/env bash`n#   Optional env vars:`n#     JIM_SETUP_AUTO_START  - start without prompting`nset -euo pipefail`nDOCS_BASE=`"https://docs.junctional.io`"`necho `"See `${DOCS_BASE}/administration/configuration/#logging`"`necho `"`$JIM_NOT_A_SETTING`"`n"
            'docs/powershell/things.md'                                = (Get-Page 'Things' "## Get-JIMThing`n`nGets a thing.`n`n| Cmdlet | Purpose |`n|--------|---------|`n| ``Set-JIMThing`` | Sets a thing. |`n")
            'docs/administration/configuration.md'                     = (Get-Page 'Configuration Reference' ("## Logging`n`n| Variable | Description |`n|----------|-------------|`n| ``JIM_LOG_LEVEL`` | How much to log. |`n| ``JIM_CACHE_SIZE`` | Cache size. |`n| ``JIM_WEB_PORT`` | The HTTPS port. |`n`n" +
                "## Installer`n`n| Variable | Description |`n|----------|-------------|`n| ``JIM_SETUP_AUTO_START`` | Start without prompting. |`n"))
            'docs/concepts/expressions.md'                             = (Get-Page 'Expressions' "## Built-in Functions`n`n| Function | Description | Example |`n|----------|-------------|---------|`n| ``Trim(value)`` | Remove spaces | ``Trim(x)`` |`n| ``Upper(value)`` | Uppercase | ``Upper(x)`` |`n")
            'docs/reference/glossary.md'                               = (Get-Page 'Glossary' "Metaverse { #metaverse }`n:   The central store.`n")
        }
    }

    function Get-DefaultConfig {
        @{
            ModuleManifest      = 'src/JIM.PowerShell/JIM.psd1'
            CmdletDocs          = 'docs/powershell'
            Constants           = 'src/JIM.Models/Core/Constants.cs'
            CodeSources         = @('src')
            PowerShellSources   = @('src/JIM.PowerShell/Public', 'src/JIM.PowerShell/Private')
            ComposeFiles        = @('docker-compose.yml', 'deploy/docker-compose.production.yml')
            EnvExample          = '.env.example'
            Installer           = 'deploy/setup.sh'
            ExpressionEvaluator = 'src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs'
            ExpressionDocs      = 'docs/concepts/expressions.md'
            LinkSources         = @('src', 'deploy', 'docker-compose.yml')
            DocsBaseUrl         = 'https://docs.junctional.io'
            DocsPath            = 'docs'
            DocsExclude         = @('CLAUDE.md', 'developer/**')
            Allow               = @{ Cmdlets = @{}; EnvironmentVariables = @{}; ExpressionFunctions = @{}; DocsLinks = @{} }
        }
    }

    function New-Repository {
        param([hashtable]$Files = @{}, [string[]]$Remove = @(), [hashtable]$Allow = @{})
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $all = Get-DefaultFiles
        foreach ($k in $Files.Keys) { $all[$k] = $Files[$k] }
        foreach ($k in $Remove) { $all.Remove($k) }
        foreach ($rel in $all.Keys) {
            $full = Join-Path $root $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            Set-Content -Path $full -Value $all[$rel] -Encoding utf8 -NoNewline
        }
        $config = Get-DefaultConfig
        foreach ($k in $Allow.Keys) { $config.Allow[$k] = $Allow[$k] }
        $configPath = Join-Path $root 'docs/.conformance/coverage.psd1'
        New-Item -ItemType Directory -Path (Split-Path $configPath -Parent) -Force | Out-Null
        Set-Content -Path $configPath -Value (ConvertTo-Psd1 -Value $config) -Encoding utf8
        $root
    }

    function Invoke-Coverage {
        param([string]$Root, [hashtable]$Environment = @{})
        $saved = @{}
        foreach ($k in @('GITHUB_ACTIONS', 'GITHUB_STEP_SUMMARY') + @($Environment.Keys)) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k)
            [Environment]::SetEnvironmentVariable($k, $null)
        }
        foreach ($k in $Environment.Keys) { [Environment]::SetEnvironmentVariable($k, $Environment[$k]) }
        try {
            Push-Location $Root
            $output = pwsh -NoProfile -File $script:ScriptPath -RepositoryRoot $Root 2>&1
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
        }
        finally {
            Pop-Location
            foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
        }
    }
}

Describe 'Test-DocsCoverage: a fully documented repository' {
    It 'passes, and says how many of each thing it checked' {
        $r = Invoke-Coverage -Root (New-Repository)
        $r.Output | Should -Match 'Docs coverage passed'
        $r.Output | Should -Match '2 cmdlets'
        $r.ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsCoverage: COV-1 exported cmdlets' {
    It 'fails an exported cmdlet with no heading or table row on a PowerShell page, naming the manifest line' {
        $files = @{ 'src/JIM.PowerShell/JIM.psd1' = "@{`n    FunctionsToExport = @(`n        'Get-JIMThing',`n        'Set-JIMThing',`n        'Remove-JIMThing'`n    )`n}`n" }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "JIM\.psd1:5\s+COV-1\s+.*'Remove-JIMThing'"
    }

    It 'fails a heading naming a cmdlet the module does not export' {
        $page = Get-Page 'Things' "## Get-JIMThing`n`nGets a thing.`n`n## Clear-JIMThing`n`nGone.`n`n| Cmdlet | Purpose |`n|---|---|`n| ``Set-JIMThing`` | Sets. |`n"
        $r = Invoke-Coverage -Root (New-Repository -Files @{ 'docs/powershell/things.md' = $page })
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "things\.md:\d+\s+COV-1\s+.*'Clear-JIMThing'.*does not export"
    }

    It 'passes an allow-listed cmdlet, and fails an allow-list entry that no longer applies' {
        $files = @{ 'src/JIM.PowerShell/JIM.psd1' = "@{ FunctionsToExport = @('Get-JIMThing', 'Set-JIMThing', 'Test-JIMHook') }" }
        (Invoke-Coverage -Root (New-Repository -Files $files -Allow @{ Cmdlets = @{ 'Test-JIMHook' = 'A test hook.' } })).ExitCode | Should -Be 0

        $r = Invoke-Coverage -Root (New-Repository -Allow @{ Cmdlets = @{ 'Get-JIMThing' = 'Was a test hook.' } })
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "COV-1.*allow-list.*'Get-JIMThing'.*no longer needed"
    }
}

Describe 'Test-DocsCoverage: COV-2 environment variables' {
    It 'fails a variable the product reads that no table documents, naming where it is read' {
        $files = @{ 'src/JIM.Worker/Worker.cs' = "var x = Environment.GetEnvironmentVariable(`"JIM_CACHE_SIZE`");`nvar y = Environment.GetEnvironmentVariable(`"JIM_WRITE_PARALLELISM`");`n" }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "Worker\.cs:2\s+COV-2\s+.*'JIM_WRITE_PARALLELISM'"
    }

    It 'reads the installer header, compose files and .env.example, but not compose escapes, help examples or other strings' {
        $files = @{
            '.env.example'    = "JIM_LOG_LEVEL=Information`n# JIM_THEME=dark`n"
            'deploy/setup.sh' = "#!/usr/bin/env bash`n#     JIM_SETUP_AUTO_START  - start`n#     JIM_SETUP_RUNTIME     - docker or podman`nset -euo pipefail`n"
        }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "COV-2\s+.*'JIM_THEME'"
        $r.Output | Should -Match "COV-2\s+.*'JIM_SETUP_RUNTIME'"
        $r.Output | Should -Not -Match "'POSTGRES_DB'|'JIM_RELATED'|'JIM_API_KEY'|'JIM_NOT_A_SETTING'"
    }

    It 'fails a documented variable that nothing reads' {
        $page = Get-Page 'Configuration Reference' "## Logging`n`n| Variable | Description |`n|---|---|`n| ``JIM_LOG_LEVEL`` | Level. |`n| ``JIM_CACHE_SIZE`` | Size. |`n| ``JIM_WEB_PORT`` | Port. |`n| ``JIM_SETUP_AUTO_START`` | Start. |`n| ``JIM_RETIRED`` | Gone. |`n"
        $r = Invoke-Coverage -Root (New-Repository -Files @{ 'docs/administration/configuration.md' = $page })
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "configuration\.md:\d+\s+COV-2\s+.*'JIM_RETIRED'.*nothing reads"
    }

    It 'passes an allow-listed variable, and fails an allow-list entry with no reason' {
        $files = @{ 'src/JIM.Worker/Worker.cs' = "var x = Environment.GetEnvironmentVariable(`"JIM_CACHE_SIZE`");`nvar g = Environment.GetEnvironmentVariable(`"JIM_OPENAPI_GENERATE`");`n" }
        (Invoke-Coverage -Root (New-Repository -Files $files -Allow @{ EnvironmentVariables = @{ 'JIM_OPENAPI_GENERATE' = 'Build-time only.' } })).ExitCode | Should -Be 0

        $r = Invoke-Coverage -Root (New-Repository -Files $files -Allow @{ EnvironmentVariables = @{ 'JIM_OPENAPI_GENERATE' = '' } })
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "'JIM_OPENAPI_GENERATE'.*reason"
    }
}

Describe 'Test-DocsCoverage: COV-6 expression functions' {
    It 'fails a registered function with no row in the function reference' {
        $files = @{ 'src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs' = "target.SetFunction(`"Trim`", x);`ntarget.SetFunction(`"Upper`", x);`ntarget.SetFunction(`"ToDecimal`", x);`n" }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "DynamicExpressoEvaluator\.cs:3\s+COV-6\s+.*'ToDecimal'"
    }

    It 'fails a documented function that is not registered' {
        $files = @{ 'src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs' = "target.SetFunction(`"Trim`", x);`n" }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "expressions\.md:\d+\s+COV-6\s+.*'Upper'"
    }
}

Describe 'Test-DocsCoverage: LNK-3 links from the product into the docs' {
    It 'passes links to existing pages, heading anchors and explicit anchors, and skips templated links' {
        $files = @{ 'src/JIM.Web/Hint.cs' = "var a = `"https://docs.junctional.io/reference/glossary/#metaverse`";`nvar b = `"https://docs.junctional.io/administration/configuration/`";`nvar c = `$`"https://docs.junctional.io/reference/glossary/#{anchor}`";`n" }
        (Invoke-Coverage -Root (New-Repository -Files $files)).ExitCode | Should -Be 0
    }

    It 'fails a link to a page that does not exist, naming the source line' {
        $files = @{ 'src/JIM.Web/Hint.cs' = "// see`nvar a = `"https://docs.junctional.io/administration/missing/`";`n" }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "Hint\.cs:2\s+LNK-3\s+.*administration/missing"
    }

    It 'fails a link to an anchor the page does not have, including one built from the installer''s DOCS_BASE' {
        $files = @{ 'deploy/setup.sh' = "#!/usr/bin/env bash`n#     JIM_SETUP_AUTO_START  - start`nset -euo pipefail`nDOCS_BASE=`"https://docs.junctional.io`"`necho `"`${DOCS_BASE}/administration/configuration/#no-such-section`"`n" }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "setup\.sh:5\s+LNK-3\s+.*#no-such-section"
    }
}

Describe 'Test-DocsCoverage: LNK-3 links followed by a shell variable' {
    It 'checks a link with a colour reset straight after it, rather than skipping it as templated' {
        $files = @{ 'deploy/setup.sh' = "#!/usr/bin/env bash`n#     JIM_SETUP_AUTO_START  - start`nset -euo pipefail`nDOCS_BASE=`"https://docs.junctional.io`"`necho `"`${DOCS_BASE}/administration/missing/`${RESET}`"`n" }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "setup\.sh:5\s+LNK-3\s+.*administration/missing/'"
    }
}

Describe 'Test-DocsCoverage: reporting' {
    It 'writes GitHub Actions annotations and a job summary' {
        $files = @{ 'src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs' = "target.SetFunction(`"Trim`", x);`ntarget.SetFunction(`"Upper`", x);`ntarget.SetFunction(`"ToDecimal`", x);`n" }
        $root = New-Repository -Files $files
        $summary = Join-Path $root 'summary.md'
        $r = Invoke-Coverage -Root $root -Environment @{ GITHUB_ACTIONS = 'true'; GITHUB_STEP_SUMMARY = $summary }
        $r.Output | Should -Match '::error file=src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs,line=3,title=COV-6::'
        (Get-Content $summary -Raw) | Should -Match '\| COV-6 \|'
    }

    It 'exits 2 when the configuration cannot be read' {
        $root = New-Repository
        Set-Content -Path (Join-Path $root 'docs/.conformance/coverage.psd1') -Value '@{ }' -Encoding utf8
        (Invoke-Coverage -Root $root).ExitCode | Should -Be 2
    }
}
