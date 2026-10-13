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
            'src/JIM.Web/Pages/Things.razor'                           = "@page `"/things`"`n@page `"/things/{Id:int}`"`n<MudText Typo=`"Typo.h3`">Things`n    <PageInfo Title=`"Things`" DocsPage=`"administration/configuration.md#logging`">`n        What the page is for.`n    </PageInfo>`n</MudText>`n"
            'src/JIM.Web/Shared/ThingChip.razor'                       = "<MudChip>@Name</MudChip>`n"
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
            ApiIndex            = 'docs/api/index.md'
            PortalSources       = @('src/JIM.Web')
            Allow               = @{ Cmdlets = @{}; EnvironmentVariables = @{}; ExpressionFunctions = @{}; DocsLinks = @{}; ApiRoutes = @{}; ApiResourceGroups = @{}; PortalPages = @{} }
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
        param([string]$Root, [hashtable]$Environment = @{}, [string[]]$ExtraArgs = @())
        $saved = @{}
        foreach ($k in @('GITHUB_ACTIONS', 'GITHUB_STEP_SUMMARY') + @($Environment.Keys)) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k)
            [Environment]::SetEnvironmentVariable($k, $null)
        }
        foreach ($k in $Environment.Keys) { [Environment]::SetEnvironmentVariable($k, $Environment[$k]) }
        try {
            Push-Location $Root
            $output = pwsh -NoProfile -File $script:ScriptPath -RepositoryRoot $Root @ExtraArgs 2>&1
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

    It "reads a function registered in one of the evaluator's partial files" {
        $files = @{
            'src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs'         = "target.SetFunction(`"Trim`", x);`n"
            'src/JIM.Application/Expressions/DynamicExpressoEvaluator.Library.cs' = "target.SetFunction(`"Upper`", x);`ntarget.SetFunction(`"Upper`", y);`ntarget.SetFunction(`"ToDecimal`", x);`n"
        }
        $r = Invoke-Coverage -Root (New-Repository -Files $files)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Not -Match "COV-6\s+.*'Upper'"
        $r.Output | Should -Match "DynamicExpressoEvaluator\.Library\.cs:3\s+COV-6\s+.*'ToDecimal'"
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

Describe 'Test-DocsCoverage: COV-5 portal pages name their docs page' {
    It 'passes a routable page whose PageInfo names a docs page and heading that exist, and needs nothing of a component that is not a page' {
        $r = Invoke-Coverage -Root (New-Repository)
        $r.Output | Should -Match 'Docs coverage passed'
        $r.Output | Should -Match '1 portal pages'
        $r.ExitCode | Should -Be 0
    }

    It 'fails a routable page with no PageInfo naming its docs page, at its @page line' {
        $page = "@using JIM.Web`n@page `"/widgets`"`n<MudText Typo=`"Typo.h3`">Widgets`n    <PageInfo Title=`"Widgets`">What the page is for.</PageInfo>`n</MudText>`n"
        $r = Invoke-Coverage -Root (New-Repository -Files @{ 'src/JIM.Web/Pages/Widgets.razor' = $page })
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'Widgets\.razor:2\s+COV-5\s+.*DocsPage'
    }

    It 'fails a DocsPage naming a page that does not exist, and one naming a heading its page does not have' {
        $missingPage = "@page `"/a`"`n<PageInfo Title=`"A`" DocsPage=`"configuration/nowhere.md`">A.</PageInfo>`n"
        $missingAnchor = "@page `"/b`"`n<PageInfo Title=`"B`" DocsPage=`"administration/configuration.md#nowhere`">B.</PageInfo>`n"
        $r = Invoke-Coverage -Root (New-Repository -Files @{ 'src/JIM.Web/Pages/A.razor' = $missingPage; 'src/JIM.Web/Pages/B.razor' = $missingAnchor })
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "A\.razor:2\s+COV-5\s+.*'configuration/nowhere\.md'"
        $r.Output | Should -Match "B\.razor:2\s+COV-5\s+.*'#nowhere'"
    }

    It 'reads a DocsPage given across lines, and checks every page an expression can choose' {
        $page = "@page `"/c`"`n<PageInfo Title=`"C`"`n          DocsPage=`"@(_x ? `"administration/configuration.md#logging`" : `"configuration/gone.md`")`">C.</PageInfo>`n"
        $r = Invoke-Coverage -Root (New-Repository -Files @{ 'src/JIM.Web/Pages/C.razor' = $page })
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "C\.razor:3\s+COV-5\s+.*'configuration/gone\.md'"
        $r.Output | Should -Not -Match "'administration/configuration\.md#logging'"
    }

    It 'fails a DocsPage expression holding no docs page, since there is nothing in it to check' {
        $page = "@page `"/d`"`n<PageInfo Title=`"D`" DocsPage=`"@(DocsPageFor(_x))`">D.</PageInfo>`n"
        $r = Invoke-Coverage -Root (New-Repository -Files @{ 'src/JIM.Web/Pages/D.razor' = $page })
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "D\.razor:2\s+COV-5\s+.*names no docs page"
    }

    It 'passes an allow-listed page, and fails an entry with no reason, one for a page that is gone and one for a page that now names its docs' {
        $bare = "@page `"/bare`"`n<MudText>Bare</MudText>`n"
        $allow = @{ PortalPages = @{
                    'src/JIM.Web/Pages/Bare.razor'   = 'A developer specimen page with nothing to document.'
                    'src/JIM.Web/Pages/Empty.razor'  = ''
                    'src/JIM.Web/Pages/Gone.razor'   = 'Was a page once.'
                    'src/JIM.Web/Pages/Things.razor' = 'Documented now.'
                } }
        $r = Invoke-Coverage -Root (New-Repository -Files @{ 'src/JIM.Web/Pages/Bare.razor' = $bare; 'src/JIM.Web/Pages/Empty.razor' = $bare } -Allow $allow)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Not -Match 'Bare\.razor:\d+\s+COV-5'
        $r.Output | Should -Match "COV-5\s+.*'src/JIM.Web/Pages/Empty.razor' gives no reason"
        $r.Output | Should -Match "COV-5\s+.*'src/JIM.Web/Pages/Gone.razor' names a page that no longer exists"
        $r.Output | Should -Match "COV-5\s+.*'src/JIM.Web/Pages/Things.razor' is no longer needed"
    }
}

Describe 'Test-DocsCoverage: ACC-3 REST routes and COV-4 resource groups' {
    BeforeAll {
        # A small OpenAPI document, shaped as the real one is.
        $script:OpenApi = @'
{
  "openapi": "3.1.1",
  "paths": {
    "/api/v1/synchronisation/connected-systems/{connectedSystemId}/clear": { "post": { "tags": ["Synchronisation"] } },
    "/api/v1/synchronisation/connected-systems/{connectedSystemId}": { "get": { "tags": ["Synchronisation"] }, "delete": { "tags": ["Synchronisation"] } },
    "/api/v1/health/ready": { "get": { "tags": ["Health"] } },
    "/api/v1/generated-values/decisions/{id}": { "get": { "tags": ["Health"] } },
    "/api/v1/generated-values/decisions/summary": { "get": { "tags": ["Health"] } }
  }
}
'@
        $script:ApiIndex = Get-Page 'API' "## Resource groups`n`n| Resource group | What it covers |`n|---|---|`n| ``Synchronisation`` | Connected Systems and their runs. |`n| ``Health`` | Whether JIM is up. |`n"
        function New-ApiRepository {
            param([string]$Page, [string]$Index = $script:ApiIndex, [hashtable]$Allow = @{})
            $root = New-Repository -Files @{ 'docs/api/index.md' = $Index; 'docs/configuration/systems.md' = (Get-Page 'Systems' $Page) } -Allow $Allow
            Set-Content -Path (Join-Path $root 'openapi.json') -Value $script:OpenApi -Encoding utf8
            $root
        }
        function Invoke-Api {
            param([string]$Root)
            Invoke-Coverage -Root $Root -ExtraArgs @('-OpenApiPath', (Join-Path $Root 'openapi.json'), '-Rule', 'ACC-3,COV-4')
        }
    }

    It 'passes full routes, with placeholders or real ids, in prose, inline code and code blocks' {
        $page = "Clear one with ``POST /api/v1/synchronisation/connected-systems/{id}/clear``.`n`n``````bash`ncurl -X DELETE https://jim.example.com/api/v1/synchronisation/connected-systems/3`n```````n`nReadiness: GET /api/v1/health/ready.`n"
        $r = Invoke-Api -Root (New-ApiRepository -Page $page)
        $r.Output | Should -Match 'Docs coverage passed'
        $r.ExitCode | Should -Be 0
    }

    It 'fails a route the API does not have, naming the page line' {
        $r = Invoke-Api -Root (New-ApiRepository -Page "Line one.`n`nCall ``GET /api/v1/synchronisation/systems``.`n")
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "systems\.md:9\s+ACC-3\s+.*'/api/v1/synchronisation/systems'"
    }

    It 'fails a method the route does not support' {
        $r = Invoke-Api -Root (New-ApiRepository -Page "Call ``PUT /api/v1/health/ready``.`n")
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "ACC-3\s+.*PUT.*GET"
    }

    It 'fails an abbreviated route, naming the full route to write' {
        $r = Invoke-Api -Root (New-ApiRepository -Page "Clear it with ``POST /connected-systems/{id}/clear``.`n")
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "ACC-3\s+.*abbreviated.*'/api/v1/synchronisation/connected-systems/\{connectedSystemId\}/clear'"
    }

    It 'leaves alone a bare version prefix such as /api/v1/' {
        $r = Invoke-Api -Root (New-ApiRepository -Page "Every endpoint is under ``/api/v1/``; the next version under ``/api/v2/``.`n")
        $r.ExitCode | Should -Be 0
    }

    It 'passes a path given without a method that is the start of a route, since it names a resource rather than a call' {
        $r = Invoke-Api -Root (New-ApiRepository -Page "Connected Systems live under ``/api/v1/synchronisation/connected-systems``, health under /api/v1/health.`n")
        $r.Output | Should -Match 'Docs coverage passed'
        $r.ExitCode | Should -Be 0
    }

    It 'fails a path without a method that starts no route, and a path with a method that is only the start of one' {
        $r = Invoke-Api -Root (New-ApiRepository -Page "Widgets live under ``/api/v1/widgets``.`n`nCall ``GET /api/v1/synchronisation/connected-systems``.`n")
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "ACC-3\s+.*'/api/v1/widgets'"
        $r.Output | Should -Match "ACC-3\s+.*'/api/v1/synchronisation/connected-systems'"
    }

    It 'names the one full route whose fixed segments match, not every route a placeholder would' {
        $r = Invoke-Api -Root (New-ApiRepository -Page "Read ``GET /generated-values/decisions/summary``.`n")
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "'/api/v1/generated-values/decisions/summary'\."
        $r.Output | Should -Not -Match 'decisions/\{id\}'
    }

    It 'leaves alone a method and path that are not a JIM route' {
        $r = Invoke-Api -Root (New-ApiRepository -Page "The identity provider answers ``POST /token`` with the tokens.`n")
        $r.ExitCode | Should -Be 0
    }

    It 'fails a resource group with no row, and a row naming a group the API does not have' {
        $index = Get-Page 'API' "## Resource groups`n`n| Resource group | What it covers |`n|---|---|`n| ``Synchronisation`` | Connected Systems. |`n| ``Widgets`` | Gone. |`n"
        $r = Invoke-Api -Root (New-ApiRepository -Page "Nothing here.`n" -Index $index)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "COV-4\s+.*'Health'"
        $r.Output | Should -Match "index\.md:\d+\s+COV-4\s+.*'Widgets'"
    }

    It 'skips the API rules, and says so, when no OpenAPI document is given' {
        $r = Invoke-Coverage -Root (New-ApiRepository -Page "Call ``GET /api/v1/synchronisation/systems``.`n")
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'ACC-3 and COV-4 skipped'
    }
}
