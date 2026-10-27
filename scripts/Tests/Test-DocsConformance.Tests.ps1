# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Test-DocsConformance.ps1.

.DESCRIPTION
    Exercises each mechanical rule of the documentation standard
    (engineering/DOCUMENTATION_STANDARD.md, #1454) against fixture pages, and
    the machinery around the rules: the shrink-only baseline, in-page exception
    markers, conformant and priority pages, exclusions, and the GitHub Actions
    annotations and job summary.

    Every fixture is written under $TestDrive with its own docs folder,
    mkdocs.yml, configuration and baseline, so none of this depends on the
    live repository's pages.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Test-DocsConformance.ps1')).Path
    $script:ShippedConfig = Import-PowerShellDataFile (Join-Path $PSScriptRoot '..' '..' 'docs' '.conformance' 'config.psd1')

    # The configuration every fixture starts from. Tests override single keys
    # rather than restating the whole shape.
    function Get-DefaultConfig {
        @{
            Exclude             = @('developer/**')
            Rules               = @{
                'STR-1' = 'Block'; 'STR-2' = 'Ratchet'; 'STR-3' = 'Block'; 'STR-4' = 'Block'; 'STR-5' = 'Block'
                'STR-6' = 'Ratchet'; 'STR-7' = 'Warn'; 'STR-8' = 'Warn'; 'MNT-3' = 'Block'
                'A11Y-1' = 'Block'; 'A11Y-2' = 'Block'; 'TYP-1' = 'Block'; 'TUT-2' = 'Block'; 'PRI-1' = 'Block'
                'LANG-1' = 'Block'; 'LANG-2' = 'Block'; 'LANG-3' = 'Block'; 'LANG-4' = 'Block'
                'LANG-5' = 'Block'; 'LANG-6' = 'Block'; 'LANG-7' = 'Block'; 'LANG-8' = 'Block'; 'TERM-1' = 'Block'
                'STY-1' = 'Block'; 'STY-3' = 'Block'
            }
            RequiredFrontMatter = @('title', 'description', 'type', 'audience')
            AllowedFrontMatter  = @('title', 'description', 'type', 'audience', 'template', 'hide', 'scenario', 'validated')
            Audiences           = @('evaluator', 'implementer', 'administrator', 'operator', 'automation-author', 'security-assessor')
            PageTypes           = @{
                'tutorial'        = @{ RequiredHeadings = @('Before you begin', 'What you achieved'); RequiredFrontMatter = @('scenario'); RequiresNumberedSteps = $true }
                'how-to'          = @{ RequiredHeadings = @('Check it worked'); RequiresNumberedSteps = $true }
                'concept'         = @{ ForbiddenTitles = @('Overview', 'Introduction', 'How it works') }
                'reference'       = @{}
                'troubleshooting' = @{ RequiredHeadings = @('Still stuck?') }
                'landing'         = @{}
                'object'          = @{ TrailingHeadings = @('Settings', 'Troubleshooting', 'Related') }
            }
            DescriptionLength   = @{ Min = 50; Max = 160 }
            SizeBudgetWords     = 5000
            PrioritySizeBudgetWords = 3000
            StubPatterns        = @('Documentation In Progress', 'Content will be added')
            PriorityPages       = @()
            ConformantFolders   = @()
            ConformantPages     = @()
            ScenarioPathFormat  = 'test/integration/scenarios/Invoke-Scenario-{0}.ps1'
            PortalLabelSources  = @('src')
            GlossaryPage        = 'reference/glossary.md'
            # The language patterns are the shipped ones, so these tests exercise what CI runs.
            AmericanSpellings   = $script:ShippedConfig.AmericanSpellings
            IzeExceptions       = $script:ShippedConfig.IzeExceptions
            AllowedSpellings    = $script:ShippedConfig.AllowedSpellings
            TimeRelativePatterns = $script:ShippedConfig.TimeRelativePatterns
            FuturePatterns      = $script:ShippedConfig.FuturePatterns
            MinimiserPatterns   = $script:ShippedConfig.MinimiserPatterns
        }
    }

    # Serialises a hashtable of strings, numbers, booleans, arrays and nested
    # hashtables as PowerShell data file text.
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
        if ($Value -is [bool]) { return $(if ($Value) { '$true' } else { '$false' }) }
        if ($Value -is [int]) { return "$Value" }
        return "'$($Value -replace "'", "''")'"
    }

    # A page that satisfies every rule; tests break one thing at a time.
    function New-Page {
        param(
            [string]$Title = 'Connected Systems',
            [string]$Type = 'reference',
            [string]$Audience = 'administrator',
            [string]$Description = 'What a Connected System is, and the settings every Connected System shares.',
            [string]$Body = "A Connected System is an external system JIM synchronises identities with.`n",
            [string[]]$OmitKeys = @(),
            [hashtable]$ExtraFrontMatter = @{}
        )
        $fm = [ordered]@{ title = $Title; description = $Description; type = $Type; audience = $Audience }
        foreach ($k in $ExtraFrontMatter.Keys) { $fm[$k] = $ExtraFrontMatter[$k] }
        $lines = foreach ($k in $fm.Keys) {
            if ($k -in $OmitKeys) { continue }
            "${k}: `"$($fm[$k])`""
        }
        "---`n$($lines -join "`n")`n---`n`n# $Title`n`n$Body"
    }

    function New-Fixture {
        param(
            [hashtable]$Pages = @{},
            [hashtable]$ConfigOverrides = @{},
            [string]$Baseline = '',
            [string]$Nav = '',
            [hashtable]$OtherFiles = @{},
            [hashtable]$Vocabulary = @{ Abbreviations = @{}; Terms = @() }
        )
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $docs = Join-Path $root 'docs'
        New-Item -ItemType Directory -Path $docs -Force | Out-Null
        foreach ($rel in $Pages.Keys) {
            $full = Join-Path $docs $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            Set-Content -Path $full -Value $Pages[$rel] -Encoding utf8 -NoNewline
        }
        foreach ($rel in $OtherFiles.Keys) {
            $full = Join-Path $root $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            Set-Content -Path $full -Value $OtherFiles[$rel] -Encoding utf8 -NoNewline
        }
        $config = Get-DefaultConfig
        foreach ($k in $ConfigOverrides.Keys) { $config[$k] = $ConfigOverrides[$k] }
        $configPath = Join-Path $root 'config.psd1'
        Set-Content -Path $configPath -Value (ConvertTo-Psd1 -Value $config) -Encoding utf8
        $baselinePath = Join-Path $root 'baseline.tsv'
        Set-Content -Path $baselinePath -Value $Baseline -Encoding utf8 -NoNewline
        Set-Content -Path (Join-Path $root 'mkdocs.yml') -Value "site_name: Test`nnav:`n$Nav" -Encoding utf8
        $vocabularyPath = Join-Path $root 'vocabulary.psd1'
        Set-Content -Path $vocabularyPath -Value (ConvertTo-Psd1 -Value $Vocabulary) -Encoding utf8
        [pscustomobject]@{ Root = $root; Docs = $docs; Config = $configPath; Baseline = $baselinePath; Vocabulary = $vocabularyPath }
    }

    # Runs the script as CI does (a child pwsh) and returns the exit code and output.
    function Invoke-Conformance {
        param($Fixture, [string[]]$ExtraArgs = @(), [hashtable]$Environment = @{})
        $saved = @{}
        foreach ($k in @('GITHUB_ACTIONS', 'GITHUB_STEP_SUMMARY', 'CHANGED_FILES', 'BASE_SHA') + @($Environment.Keys)) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k)
            [Environment]::SetEnvironmentVariable($k, $null)
        }
        foreach ($k in $Environment.Keys) { [Environment]::SetEnvironmentVariable($k, $Environment[$k]) }
        try {
            Push-Location $Fixture.Root
            $output = pwsh -NoProfile -File $script:ScriptPath -DocsPath $Fixture.Docs -ConfigPath $Fixture.Config `
                -BaselinePath $Fixture.Baseline -MkDocsConfigPath (Join-Path $Fixture.Root 'mkdocs.yml') `
                -VocabularyPath $Fixture.Vocabulary -RepositoryRoot $Fixture.Root @ExtraArgs 2>&1
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
        }
        finally {
            Pop-Location
            foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
        }
    }
}

Describe 'Test-DocsConformance: a conformant page' {
    It 'passes a page that satisfies every rule' {
        $f = New-Fixture -Pages @{ 'connected-systems.md' = (New-Page) } -Nav '  - Connected Systems: connected-systems.md'
        $r = Invoke-Conformance -Fixture $f
        $r.Output | Should -Match 'Docs conformance passed'
        $r.ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: STR-1 front matter is valid' {
    It 'fails an unquoted value containing ": ", naming the line and the fix' {
        $page = (New-Page) -replace 'description: "What a Connected System is, and the settings every Connected System shares."', 'description: What it is: the settings every Connected System shares, for administrators.'
        $f = New-Fixture -Pages @{ 'cs.md' = $page }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'STR-1'
        $r.Output | Should -Match 'cs\.md:3'
        $r.Output | Should -Match 'quote'
    }

    It 'fails an unknown front matter key' {
        $page = (New-Page) -replace 'audience:', 'audiance:'
        $f = New-Fixture -Pages @{ 'cs.md' = $page }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "STR-1.*'audiance'"
    }

    It 'fails a type that is not one of the page types' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Type 'guide') }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "STR-1.*'guide' is not a page type"
    }

    It 'fails a description longer than the maximum' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Description ('x' * 170)) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'STR-1.*170 characters'
    }

    It 'accepts a quoted value containing ": " and a list value' {
        $page = (New-Page -Title 'SSO Setup: Keycloak') -replace '---\n\n# ', "hide:`n  - navigation`n---`n`n# "
        $f = New-Fixture -Pages @{ 'keycloak.md' = $page }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: STR-2 required front matter, and the shrink-only baseline' {
    It 'fails a new page with no type, which has no baseline allowance' {
        $f = New-Fixture -Pages @{ 'generated-values.md' = (New-Page -OmitKeys 'type') }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "generated-values\.md:1.*STR-2.*front matter has no 'type'"
    }

    It 'passes an existing page whose missing keys match its baseline' {
        $f = New-Fixture -Pages @{ 'old.md' = (New-Page -OmitKeys 'type', 'audience') } -Baseline "STR-2`told.md`t2`n"
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 0
    }

    It 'fails when a page has more findings than its baseline allows' {
        $f = New-Fixture -Pages @{ 'old.md' = (New-Page -OmitKeys 'type', 'audience') } -Baseline "STR-2`told.md`t1`n"
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'STR-2.*2 found, the baseline allows 1'
    }

    It 'fails when a page has fewer findings than its baseline, until the improvement is locked in' {
        $f = New-Fixture -Pages @{ 'old.md' = (New-Page -OmitKeys 'type') } -Baseline "STR-2`told.md`t2`n"
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'UpdateBaseline'

        $u = Invoke-Conformance -Fixture $f -ExtraArgs @('-UpdateBaseline')
        $u.ExitCode | Should -Be 0
        (Get-Content $f.Baseline -Raw) | Should -Match "STR-2\told\.md\t1"

        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }

    It 'never adds a baseline entry for a new page when updating' {
        $f = New-Fixture -Pages @{ 'new.md' = (New-Page -OmitKeys 'type') }
        $u = Invoke-Conformance -Fixture $f -ExtraArgs @('-UpdateBaseline')
        $u.ExitCode | Should -Be 1
        (Get-Content $f.Baseline -Raw) | Should -Not -Match 'new\.md'
    }

    It 'fails a baseline entry for a page that no longer exists, and updating removes it' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page) } -Baseline "STR-2`tgone.md`t3`n"
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'gone\.md'

        (Invoke-Conformance -Fixture $f -ExtraArgs @('-UpdateBaseline')).ExitCode | Should -Be 0
        (Get-Content $f.Baseline -Raw) | Should -Not -Match 'gone\.md'
    }

    It 'takes the lowest count when a merge leaves two entries for the same rule and page' {
        $f = New-Fixture -Pages @{ 'old.md' = (New-Page -OmitKeys 'type') } -Baseline "STR-2`told.md`t1`nSTR-2`told.md`t3`n"
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 0
    }

    It 'seeds a rule that has no entries yet, and refuses to reseed one that has' {
        $f = New-Fixture -Pages @{ 'a.md' = (New-Page -OmitKeys 'type'); 'b.md' = (New-Page -OmitKeys 'type', 'audience') }
        $s = Invoke-Conformance -Fixture $f -ExtraArgs @('-SeedBaseline', '-Rule', 'STR-2')
        $s.ExitCode | Should -Be 0
        $text = Get-Content $f.Baseline -Raw
        $text | Should -Match "STR-2\ta\.md\t1"
        $text | Should -Match "STR-2\tb\.md\t2"
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0

        $again = Invoke-Conformance -Fixture $f -ExtraArgs @('-SeedBaseline', '-Rule', 'STR-2')
        $again.ExitCode | Should -Be 1
        $again.Output | Should -Match 'already has baseline entries'
    }
}

Describe 'Test-DocsConformance: STR-3 one name for the page' {
    It 'fails a title that differs from the H1' {
        $page = (New-Page -Title 'Deployment') -replace '# Deployment', '# Deployment Guide'
        $f = New-Fixture -Pages @{ 'deployment.md' = $page }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "STR-3.*'Deployment Guide'.*'Deployment'"
    }

    It 'accepts a nav label that drops leading words of the name, and fails a different label' {
        $page = New-Page -Title 'SSO Setup: Keycloak'
        $ok = New-Fixture -Pages @{ 'keycloak.md' = $page } -Nav '  - Keycloak: keycloak.md'
        (Invoke-Conformance -Fixture $ok).ExitCode | Should -Be 0

        $qualified = New-Fixture -Pages @{ 'auth.md' = (New-Page -Title 'API Authentication') } -Nav '  - Authentication: auth.md'
        (Invoke-Conformance -Fixture $qualified).ExitCode | Should -Be 0

        $partWord = New-Fixture -Pages @{ 'roadmap.md' = (New-Page -Title 'Product Roadmap') } -Nav '  - map: roadmap.md'
        (Invoke-Conformance -Fixture $partWord).ExitCode | Should -Be 1

        $bad = New-Fixture -Pages @{ 'keycloak.md' = $page } -Nav '  - Keycloak SSO: keycloak.md'
        $r = Invoke-Conformance -Fixture $bad
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "STR-3.*nav label 'Keycloak SSO'"
    }
}

Describe 'Test-DocsConformance: STR-4 heading structure' {
    It 'fails a second H1 and a skipped heading level' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Opening paragraph.`n`n#### Too deep`n`n# Another title`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'STR-4.*skips from H1 to H4'
        $r.Output | Should -Match 'STR-4.*2 H1 headings'
    }

    It 'ignores headings inside fenced code, and accepts a templated page with no H1' {
        $templated = "---`ntitle: `"Home`"`ndescription: `"JIM, the self-hosted identity lifecycle manager, and where to start reading about it.`"`ntype: `"landing`"`naudience: `"evaluator`"`ntemplate: `"home.html`"`n---`n"
        $f = New-Fixture -Pages @{
            'index.md' = $templated
            'cs.md'    = (New-Page -Body "Opening paragraph.`n`n``````powershell`n# a comment, not a heading`n```````n")
        }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: STR-5 code fences declare a language' {
    It 'fails a fence with no language, including an indented one' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Opening paragraph.`n`n!!! note`n    ```````n    some output`n    ```````n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'cs\.md:13.*STR-5'
    }
}

Describe 'Test-DocsConformance: STR-6 no stub pages' {
    It 'fails a new page carrying placeholder text' {
        $f = New-Fixture -Pages @{ 'first.md' = (New-Page -Body "!!! info `"Documentation In Progress`"`n    Content will be added in a future update.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'STR-6'
    }
}

Describe 'Test-DocsConformance: warnings (STR-7 opening paragraph, STR-8 size budget)' {
    It 'warns, without failing, when a page opens with a heading instead of a paragraph' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "## Straight into a section`n`nText.`n") }
        $r = Invoke-Conformance -Fixture $f -ExtraArgs @('-ShowWarnings')
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'WARNING.*cs\.md:8.*STR-7'
    }

    It 'warns when a page is over its size budget' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body (('word ' * 80) + "`n")) } -ConfigOverrides @{ SizeBudgetWords = 50 }
        $r = Invoke-Conformance -Fixture $f -ExtraArgs @('-ShowWarnings')
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'WARNING.*STR-8.*over its 50-word budget'
    }

    It 'lists warnings only for changed pages in CI, but always counts them' {
        $f = New-Fixture -Pages @{
            'a.md' = (New-Page -Body "## Heading first`n")
            'b.md' = (New-Page -Body "## Heading first`n")
        }
        $r = Invoke-Conformance -Fixture $f -Environment @{ CHANGED_FILES = 'docs/a.md' }
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'a\.md:8.*STR-7'
        $r.Output | Should -Not -Match 'b\.md:8.*STR-7'
    }
}

Describe 'Test-DocsConformance: MNT-3 an over-budget page cannot grow' {
    BeforeAll {
        function New-GitFixture {
            param([string]$BasePage, [string]$HeadPage, [int]$Budget = 50)
            $f = New-Fixture -Pages @{ 'cs.md' = $BasePage } -ConfigOverrides @{ SizeBudgetWords = $Budget }
            Push-Location $f.Root
            try {
                git init -q . 2>&1 | Out-Null
                git -c user.email=t@t -c user.name=t -c commit.gpgsign=false add -A 2>&1 | Out-Null
                git -c user.email=t@t -c user.name=t -c commit.gpgsign=false commit -q -m base 2>&1 | Out-Null
                $base = (git rev-parse HEAD).Trim()
            }
            finally { Pop-Location }
            Set-Content -Path (Join-Path $f.Docs 'cs.md') -Value $HeadPage -Encoding utf8 -NoNewline
            $f | Add-Member -NotePropertyName BaseSha -NotePropertyValue $base -PassThru
        }
    }

    It 'fails a page that was over budget on the base branch and has grown' {
        $f = New-GitFixture -BasePage (New-Page -Body (('word ' * 80) + "`n")) -HeadPage (New-Page -Body (('word ' * 95) + "`n"))
        $r = Invoke-Conformance -Fixture $f -ExtraArgs @('-BaseRef', $f.BaseSha)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'MNT-3.*grew from \d+ to \d+ words'
    }

    It 'passes an over-budget page that shrank, and a page that only now crosses the budget' {
        $shrank = New-GitFixture -BasePage (New-Page -Body (('word ' * 80) + "`n")) -HeadPage (New-Page -Body (('word ' * 70) + "`n"))
        (Invoke-Conformance -Fixture $shrank -ExtraArgs @('-BaseRef', $shrank.BaseSha)).ExitCode | Should -Be 0

        $crossed = New-GitFixture -BasePage (New-Page -Body (('word ' * 10) + "`n")) -HeadPage (New-Page -Body (('word ' * 90) + "`n"))
        (Invoke-Conformance -Fixture $crossed -ExtraArgs @('-BaseRef', $crossed.BaseSha)).ExitCode | Should -Be 0
    }

    It 'refuses an exception marker for MNT-3, which would outlive the change it excused' {
        # MNT-3 compares with the base branch, so once the growth merges the marker suppresses
        # nothing, and every later pull request would fail on it.
        $marker = "<!-- conformance: allow-page MNT-3 `"the feature needs the room`" -->`n"
        $f = New-GitFixture -BasePage (New-Page -Body (('word ' * 80) + "`n")) -HeadPage (New-Page -Body ($marker + ('word ' * 95) + "`n"))
        $r = Invoke-Conformance -Fixture $f -ExtraArgs @('-BaseRef', $f.BaseSha)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'MNT-3.*grew from \d+ to \d+ words'
        $r.Output | Should -Match 'EXC-1.*MNT-3.*cannot be excused'
    }
}

Describe 'Test-DocsConformance: accessibility (A11Y-1, A11Y-2)' {
    It 'fails a Mermaid diagram without accTitle and accDescr, and passes one with both' {
        $bad = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Opening paragraph.`n`n``````mermaid`nflowchart TD`n    A --> B`n```````n") }
        $r = Invoke-Conformance -Fixture $bad
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'A11Y-1'

        $good = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Opening paragraph.`n`n``````mermaid`nflowchart TD`n    accTitle: Two steps`n    accDescr: A leads to B.`n    A --> B`n```````n") }
        (Invoke-Conformance -Fixture $good).ExitCode | Should -Be 0
    }

    It 'fails an image without alt text, and an included diagram without a desc' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Opening paragraph.`n`n![](assets/x.png)`n`n--8<-- `"assets/diagrams/d.svg`"`n") } `
            -OtherFiles @{ 'docs/assets/diagrams/d.svg' = '<svg role="img"><title>Diagram</title></svg>' }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'cs\.md:12.*A11Y-2.*alt text'
        $r.Output | Should -Match 'cs\.md:14.*A11Y-2.*<desc>'
    }
}

Describe 'Test-DocsConformance: TYP-1 page shape' {
    It 'fails a how-to page with no "Check it worked" section, and passes one with it' {
        $bad = New-Fixture -Pages @{ 'how.md' = (New-Page -Type 'how-to' -Body "Do this when you need to.`n`n1. Open the page.`n2. Select Save.`n") }
        $r = Invoke-Conformance -Fixture $bad
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "TYP-1.*'Check it worked'"

        $good = New-Fixture -Pages @{ 'how.md' = (New-Page -Type 'how-to' -Body "Do this when you need to.`n`n1. Open the page.`n2. Select Save.`n`n## Check it worked`n`nThe page shows the change.`n") }
        (Invoke-Conformance -Fixture $good).ExitCode | Should -Be 0
    }

    It 'fails a tutorial whose scenario script does not exist' {
        $body = "You will run a synchronisation.`n`n## Before you begin`n`nInstall JIM.`n`n1. Do it.`n`n## What you achieved`n`nA synchronisation.`n"
        $f = New-Fixture -Pages @{ 'first.md' = (New-Page -Type 'tutorial' -Body $body -ExtraFrontMatter @{ scenario = '099-Missing' }) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "TYP-1.*scenario '099-Missing'"
    }

    It 'fails an object page whose Troubleshooting section comes before its Settings' {
        $body = "What it is and why it matters.`n`n## Create one`n`nSteps.`n`n## Troubleshooting`n`nFixes.`n`n## Settings`n`nTable.`n"
        $f = New-Fixture -Pages @{ 'obj.md' = (New-Page -Type 'object' -Body $body) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "TYP-1.*'Settings'.*'Troubleshooting'"
    }
}

Describe 'Test-DocsConformance: TUT-2 portal labels a tutorial names exist' {
    BeforeAll {
        $script:Portal = @{
            'src/JIM.Web/Pages/Systems.razor' = "<MudButton>Create Connected System</MudButton>`n<MudTab Text=`"Run Profiles`" />`n"
            'src/JIM.Connectors/File/FileConnector.cs' = "Name = `"File Path`",`n"
            'test/Fake.razor' = "<MudButton>Only In A Test</MudButton>`n"
        }
        function New-Tutorial {
            param([string]$Steps)
            $body = "You will run a synchronisation.`n`n## Before you begin`n`nSign in as an administrator.`n`n## Step 1: Create it`n`n$Steps`n`n## What you achieved`n`nA synchronisation.`n"
            New-Page -Type 'tutorial' -Title 'First Sync' -Body $body -ExtraFrontMatter @{ scenario = '027-First' }
        }
        $script:Scenario = @{ 'test/integration/scenarios/Invoke-Scenario-027-First.ps1' = '# scenario' }
    }

    It 'passes bold labels in a Portal tab that the portal source has, whatever their case' {
        $steps = "=== `"Portal`"`n`n    1. Select **Create Connected System**, then the **run profiles** tab.`n    2. Set **File Path**.`n"
        $f = New-Fixture -Pages @{ 'first.md' = (New-Tutorial $steps) } -OtherFiles ($script:Portal + $script:Scenario)
        $r = Invoke-Conformance -Fixture $f
        $r.Output | Should -Not -Match 'TUT-2'
        $r.ExitCode | Should -Be 0
    }

    It 'fails a bold label in a Portal tab that the portal source does not have, naming it and its line' {
        $steps = "=== `"Portal`"`n`n    1. Select **Create Connected System**.`n    2. Select **Import Schema**.`n"
        $f = New-Fixture -Pages @{ 'first.md' = (New-Tutorial $steps) } -OtherFiles ($script:Portal + $script:Scenario)
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "first\.md:\d+\s+TUT-2\s+.*'Import Schema'"
        $r.Output | Should -Not -Match "'Create Connected System'"
    }

    It 'reads only the portal sources it is given, not the tests' {
        $steps = "=== `"Portal`"`n`n    1. Select **Only In A Test**.`n"
        $f = New-Fixture -Pages @{ 'first.md' = (New-Tutorial $steps) } -OtherFiles ($script:Portal + $script:Scenario)
        (Invoke-Conformance -Fixture $f).Output | Should -Match "TUT-2.*'Only In A Test'"
    }

    It 'leaves bold text outside a Portal tab, and on other page types, alone' {
        $steps = "**Nothing here** is a portal label.`n`n=== `"PowerShell`"`n`n    Run **this** cmdlet.`n"
        $f = New-Fixture -Pages @{
            'first.md' = (New-Tutorial $steps)
            'ref.md'   = (New-Page -Title 'Reference' -Body "A reference page.`n`n=== `"Portal`"`n`n    Select **Not A Label**.`n")
        } -OtherFiles ($script:Portal + $script:Scenario)
        (Invoke-Conformance -Fixture $f).Output | Should -Not -Match 'TUT-2'
    }

    It 'lets a marker excuse a label the portal builds from data' {
        $steps = "=== `"Portal`"`n`n    <!-- conformance: allow TUT-2 `"the switch names the Metaverse Object Type`" -->`n    1. Turn on **Project Users to the Metaverse?**.`n"
        $f = New-Fixture -Pages @{ 'first.md' = (New-Tutorial $steps) } -OtherFiles ($script:Portal + $script:Scenario)
        $r = Invoke-Conformance -Fixture $f
        $r.Output | Should -Not -Match 'TUT-2'
        $r.ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: EXC-1 exception markers' {
    It 'suppresses a finding on the next line when the marker gives a reason' {
        $body = "Opening paragraph.`n`n<!-- conformance: allow STR-5 `"terminal output with no language`" -->`n```````noutput`n```````n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }

    It 'fails a marker with no reason' {
        $body = "Opening paragraph.`n`n<!-- conformance: allow STR-5 -->`n```````noutput`n```````n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'EXC-1.*reason'
    }

    It 'fails a marker naming a rule that does not exist' {
        $body = "Opening paragraph.`n`n<!-- conformance: allow STR-99 `"because`" -->`nText.`n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "EXC-1.*'STR-99'"
    }

    It 'fails a marker that suppresses nothing' {
        $body = "Opening paragraph.`n`n<!-- conformance: allow STR-5 `"no longer needed`" -->`n``````text`noutput`n```````n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'EXC-1.*suppresses nothing'
    }

    It 'suppresses a page-level finding with allow-page' {
        $page = (New-Page -OmitKeys 'type') + "`n<!-- conformance: allow-page STR-2 `"typed in the folder migration`" -->`n"
        $f = New-Fixture -Pages @{ 'cs.md' = $page }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: conformant and priority pages' {
    It 'fails a conformant page that still carries a baseline entry' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -OmitKeys 'type') } -Baseline "STR-2`tcs.md`t1`n" -ConfigOverrides @{ ConformantPages = @('cs.md') }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "STR-2.*front matter has no 'type'"
    }

    It 'treats every page in a conformant folder as conformant' {
        $f = New-Fixture -Pages @{ 'concepts/a.md' = (New-Page -OmitKeys 'type') } -Baseline "STR-2`tconcepts/a.md`t1`n" -ConfigOverrides @{ ConformantFolders = @('concepts') }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
    }

    It 'turns warnings into errors, and applies the priority budget, on a conformant priority page' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "## Heading first`n`n$('word ' * 60)`n") } `
            -ConfigOverrides @{ PriorityPages = @('cs.md'); ConformantPages = @('cs.md'); SizeBudgetWords = 100; PrioritySizeBudgetWords = 40 }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'ERROR.*STR-7'
        $r.Output | Should -Match 'ERROR.*STR-8.*40-word budget'
    }
}

Describe 'Test-DocsConformance: PRI-1 priority pages record the release they were checked against' {
    BeforeAll {
        $script:Priority = @{ PriorityPages = @('cs.md'); ConformantPages = @('cs.md') }
        $script:Version = @{ 'VERSION' = "0.16.0`n" }
    }

    It 'passes a conformant priority page validated against a released version' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -ExtraFrontMatter @{ validated = 'v0.16.0' }) } -ConfigOverrides $script:Priority -OtherFiles $script:Version
        $r = Invoke-Conformance -Fixture $f
        $r.Output | Should -Match 'Docs conformance passed'
        $r.ExitCode | Should -Be 0
    }

    It 'fails a conformant priority page that records no validation' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page) } -ConfigOverrides $script:Priority -OtherFiles $script:Version
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "ERROR.*PRI-1.*no 'validated'"
    }

    It 'does not yet ask a priority page that is not conformant for a validation' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page) } -ConfigOverrides @{ PriorityPages = @('cs.md') } -OtherFiles $script:Version
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }

    It 'fails a validation recorded on a page that is not a priority page' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -ExtraFrontMatter @{ validated = 'v0.16.0' }) } -OtherFiles $script:Version
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'PRI-1.*only a priority page'
    }

    It 'fails a validation that does not name a release as vX.Y.Z' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -ExtraFrontMatter @{ validated = '0.16' }) } -ConfigOverrides $script:Priority -OtherFiles $script:Version
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'PRI-1.*vX\.Y\.Z'
    }

    It 'fails a validation against a release later than the current version' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -ExtraFrontMatter @{ validated = 'v0.17.0' }) } -ConfigOverrides $script:Priority -OtherFiles $script:Version
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'PRI-1.*v0\.17\.0.*0\.16\.0'
    }

    It 'accepts a pre-release version' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -ExtraFrontMatter @{ validated = 'v0.16.0-alpha' }) } -ConfigOverrides $script:Priority -OtherFiles $script:Version
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }

    It 'cannot be excused by a marker' {
        $page = New-Page -Body "<!-- conformance: allow-page PRI-1 `"not checked yet`" -->`n`nA Connected System is an external system JIM synchronises identities with.`n"
        $f = New-Fixture -Pages @{ 'cs.md' = $page } -ConfigOverrides $script:Priority -OtherFiles $script:Version
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "PRI-1.*no 'validated'"
    }
}

Describe 'Test-DocsConformance: scope and reporting' {
    It 'does not check excluded pages' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page); 'developer/notes.md' = "# No front matter`n" }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }

    It 'emits GitHub annotations for errors' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -OmitKeys 'type') }
        $r = Invoke-Conformance -Fixture $f -Environment @{ GITHUB_ACTIONS = 'true' }
        $r.Output | Should -Match '::error file=docs/cs\.md,line=1,title=STR-2::'
    }

    It 'writes a per-rule table to the job summary' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -OmitKeys 'type') } -Baseline "STR-2`tcs.md`t1`n"
        $summary = Join-Path $f.Root 'summary.md'
        (Invoke-Conformance -Fixture $f -Environment @{ GITHUB_STEP_SUMMARY = $summary }).ExitCode | Should -Be 0
        $text = Get-Content $summary -Raw
        $text | Should -Match '\| STR-2 \|'
        $text | Should -Match 'Docs conformance'
    }
}

Describe 'Test-DocsConformance: LANG-1 no em dash, and no -- as a dash' {
    It 'fails an em dash in running text' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Imports run first $([char]0x2014) then exports.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'cs\.md:10\s+LANG-1.*em dash'
    }

    It 'fails a spaced double hyphen, which the site shows as two hyphens' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Imports run first -- then exports.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'LANG-1.*two hyphens'
    }

    It 'fails an unspaced double hyphen in a range' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "PostgreSQL needs a further 1--2 GB.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "LANG-1.*'1--2'.*'1 to 2'"
    }

    It 'passes a double hyphen in code, and a snippet include' {
        $body = "Run ``jim -- run`` to start.`n`n--8<-- `"includes/note.md`"`n`n``````bash`njim -- run`n```````n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: LANG-2 British English' {
    It 'fails American spellings, naming the British form' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Use organization-level consent; the behavior is the same.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "LANG-2.*'organization'.*'organisation'"
        $r.Output | Should -Match "LANG-2.*'behavior'.*'behaviour'"
    }

    It 'passes British spellings, words that only look American, and protocol names' {
        $body = "Organisation size is fixed, and the behaviour is the same. Redeem the authorization code against the Global Catalog.`n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: LANG-3 product nouns in Title Case, and TERM-1 the vocabulary' {
    BeforeAll {
        $script:Glossary = New-Page -Title 'Glossary' -Description 'The product nouns JIM uses, each defined once, with an anchor to link to.' -Body (
            "Metaverse { #metaverse }`n:   The central store of every object JIM manages.`n`n" +
            "Metaverse Object (MVO) { #metaverse-object }`n:   One object in the Metaverse.`n`n" +
            "Synchronisation Rule { #synchronisation-rule }`n:   How objects flow between systems.`n`n" +
            "Schedule { #schedule }`n:   When work starts.`n")
        function New-Vocabulary {
            param([object[]]$Terms)
            if (-not $Terms) {
                $Terms = @(
                    @{ Anchor = 'metaverse'; Home = 'reference/glossary.md'; TitleCase = $true }
                    @{ Anchor = 'metaverse-object'; Home = 'reference/glossary.md'; TitleCase = $true }
                    @{ Anchor = 'synchronisation-rule'; Home = 'reference/glossary.md'; TitleCase = $true; Rejected = @('Sync Rule') }
                    @{ Anchor = 'schedule'; Home = 'reference/glossary.md'; TitleCase = $false }
                )
            }
            @{ Abbreviations = @{ MVO = 'Metaverse Object' }; Terms = $Terms }
        }
        function New-TermFixture {
            param([string]$Body, [object[]]$Terms)
            New-Fixture -Pages @{ 'reference/glossary.md' = $script:Glossary; 'cs.md' = (New-Page -Body $Body) } -Vocabulary (New-Vocabulary -Terms $Terms)
        }
    }

    It 'fails a product noun in lower case, naming its canonical form' {
        $r = Invoke-Conformance -Fixture (New-TermFixture -Body "Every object lands in the metaverse first.`n")
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "cs\.md:10\s+LANG-3.*'metaverse'.*'Metaverse'"
    }

    It 'counts a lower-case multi-word noun once, not once per noun inside it' {
        $r = Invoke-Conformance -Fixture (New-TermFixture -Body "Each metaverse object is checked.`n")
        $r.ExitCode | Should -Be 1
        @([regex]::Matches($r.Output, 'LANG-3')).Count | Should -Be 1
        $r.Output | Should -Match "'metaverse object'.*'Metaverse Object'"
    }

    It 'passes canonical forms, plurals, code and link targets' {
        $body = "The Metaverse holds Metaverse Objects; see [the store](metaverse.md) and ``metaverse``.`n"
        (Invoke-Conformance -Fixture (New-TermFixture -Body $body)).ExitCode | Should -Be 0
    }

    It 'leaves alone a noun the vocabulary does not hold to Title Case' {
        (Invoke-Conformance -Fixture (New-TermFixture -Body "Runs start on a schedule.`n")).ExitCode | Should -Be 0
    }

    It 'fails a rejected variant, naming the canonical term' {
        $r = Invoke-Conformance -Fixture (New-TermFixture -Body "Each Sync Rule maps attributes.`n")
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "LANG-3.*'Sync Rule'.*'Synchronisation Rule'"
    }

    It 'passes when the vocabulary and the glossary agree' {
        (Invoke-Conformance -Fixture (New-TermFixture -Body "The Metaverse holds every object.`n")).ExitCode | Should -Be 0
    }

    It 'fails a glossary entry the vocabulary does not hold' {
        $terms = @(
            @{ Anchor = 'metaverse'; Home = 'reference/glossary.md'; TitleCase = $true }
            @{ Anchor = 'metaverse-object'; Home = 'reference/glossary.md'; TitleCase = $true }
            @{ Anchor = 'synchronisation-rule'; Home = 'reference/glossary.md'; TitleCase = $true }
        )
        $r = Invoke-Conformance -Fixture (New-TermFixture -Body "The Metaverse holds every object.`n" -Terms $terms)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "glossary\.md:\d+\s+TERM-1.*'Schedule'.*vocabulary"
    }

    It 'fails a vocabulary entry the glossary does not define, and a home page that is not a page' {
        $terms = @(
            @{ Anchor = 'metaverse'; Home = 'reference/glossary.md'; TitleCase = $true }
            @{ Anchor = 'metaverse-object'; Home = 'reference/glossary.md'; TitleCase = $true }
            @{ Anchor = 'synchronisation-rule'; Home = 'reference/glossary.md'; TitleCase = $true }
            @{ Anchor = 'schedule'; Home = 'missing.md'; TitleCase = $false }
            @{ Anchor = 'run-profile'; Home = 'reference/glossary.md'; TitleCase = $true }
        )
        $r = Invoke-Conformance -Fixture (New-TermFixture -Body "The Metaverse holds every object.`n" -Terms $terms)
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "vocabulary\.psd1:\d+\s+TERM-1.*'run-profile'.*glossary"
        $r.Output | Should -Match "vocabulary\.psd1:\d+\s+TERM-1.*'missing\.md'"
    }
}

Describe 'Test-DocsConformance: LANG-4 abbreviations only in table headers' {
    BeforeAll {
        $script:AbbreviationVocabulary = @{ Abbreviations = @{ MVO = 'Metaverse Object' }; Terms = @() }
    }

    It 'fails an abbreviation in running text, naming the full term' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Each MVO is checked.`n") } -Vocabulary $script:AbbreviationVocabulary
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "LANG-4.*'MVO'.*'Metaverse Object'"
    }

    It 'passes an abbreviation in a table header, and where the full term introduces it' {
        $body = "A Metaverse Object (MVO) is one object.`n`n| Type | MVOs |`n|------|------|`n| Person | 12 |`n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) } -Vocabulary $script:AbbreviationVocabulary
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }

    It 'fails an abbreviation in a table body cell' {
        $body = "Each row is a type.`n`n| Type | Kind |`n|------|------|`n| Person | MVO |`n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) } -Vocabulary $script:AbbreviationVocabulary
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'cs\.md:14\s+LANG-4'
    }
}

Describe 'Test-DocsConformance: LANG-5 no time-relative words' {
    It 'fails wording that is relative to when it was written' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Until now, imports ran alone. From this release they run in parallel.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        @([regex]::Matches($r.Output, 'LANG-5')).Count | Should -Be 2
    }

    It 'passes "now" as an instruction, and the release marker' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Select **Run now**. Available from v0.17.0.`n") }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: LANG-6 no promises about the future' {
    It 'fails a promise' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Group writeback is planned for a later release.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'LANG-6.*roadmap'
    }

    It 'passes ordinary uses of "planned", and leaves placeholder text to STR-6' {
        $body = "Arrange a planned outage first.`n`n!!! warning`n    Content will be added in a future update.`n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) } -Baseline "STR-6`tcs.md`t1`n"
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Not -Match 'LANG-6'
    }
}

Describe 'Test-DocsConformance: LANG-7 no minimisers' {
    It 'fails "simply"' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Simply restart the Worker.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "LANG-7.*'Simply'"
    }

    It 'passes "just", which is usually not a minimiser' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "The Worker has just restarted.`n") }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: STY-1 no emoji' {
    It 'fails an emoji before a heading' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "## $([char]::ConvertFromUtf32(0x1F3D7))$([char]0xFE0F) Architecture`n`nJIM follows a hub-and-spoke pattern.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'cs\.md:10\s+STY-1.*emoji'
    }

    It 'fails a tick or a cross in a table cell, naming the icon to use' {
        $body = "| Feature | Supported |`n|---|---|`n| Export | $([char]0x2705) |`n| Paging | $([char]0x274C) |`n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        @($r.Output -split "`n" | Where-Object { $_ -match 'STY-1' }).Count | Should -Be 2
        $r.Output | Should -Match 'STY-1.*check-circle-outline'
    }

    It 'fails a warning sign and its variation selector as one emoji' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "$([char]0x26A0)$([char]0xFE0F) See below.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        @($r.Output -split "`n" | Where-Object { $_ -match 'STY-1' }).Count | Should -Be 1
    }

    It 'fails an emoji outside the pictograph blocks, such as a play button or a return arrow' {
        $body = "## $([char]0x25B6)$([char]0xFE0F) Setting a password`n`nText.`n`n## $([char]0x21A9)$([char]0xFE0F) Rolling back`n`nText.`n"
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body $body) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        @($r.Output -split "`n" | Where-Object { $_ -match 'STY-1' }).Count | Should -Be 2
    }

    It 'fails any character given emoji presentation by a variation selector' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "Licensed $([char]0x00A9)$([char]0xFE0F) Tetron.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'STY-1'
    }

    It 'passes Material icons, typographic symbols, and emoji in code' {
        $body = "## :material-layers-outline: Architecture`n`n| Export | :material-check-circle-outline:{ .jim-yes } |`n|---|---|`n`nJIM$([char]0x2122) $([char]0x00A9) Tetron. Import $([char]0x2192) Sync $([char]0x2194) Export.`n`nRun ``echo $([char]0x2705)`` to test.`n`n``````text`n$([char]0x2705) done`n```````n"
        $f = New-Fixture -Pages @{ 'guide/index.md' = (New-Page -Type 'landing' -Body $body) }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: STY-3 icons on index-page H2s, and only there' {
    It 'fails an H2 with no icon on a section index page' {
        $body = "Opening paragraph.`n`n## :material-layers-outline: Architecture`n`nText.`n`n## Versioning`n`nText.`n"
        $f = New-Fixture -Pages @{ 'guide/index.md' = (New-Page -Type 'landing' -Body $body) }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "index\.md:16\s+STY-3.*'Versioning'"
        @($r.Output -split "`n" | Where-Object { $_ -match 'STY-3' }).Count | Should -Be 1
    }

    It 'fails an icon that is not a Material Design icon on a section index page' {
        $f = New-Fixture -Pages @{ 'guide/index.md' = (New-Page -Type 'landing' -Body "Opening paragraph.`n`n## :octicons-stack-24: Architecture`n`nText.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'STY-3'
    }

    It 'fails a heading icon on an article page, and on a lower heading of an index page' {
        $f = New-Fixture -Pages @{
            'cs.md'          = (New-Page -Body "Opening paragraph.`n`n## :material-tag-outline: The order`n`nText.`n")
            'guide/index.md' = (New-Page -Type 'landing' -Body "Opening paragraph.`n`n## :material-sync: Synchronisation`n`n### :material-cog-outline: Run Profiles`n`nText.`n")
        }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "cs\.md:\d+\s+STY-3.*'The order'"
        $r.Output | Should -Match "index\.md:\d+\s+STY-3.*'Run Profiles'"
    }

    It 'passes icons on every index-page H2, plain article headings, and an index page with no H2' {
        $f = New-Fixture -Pages @{
            'index.md'       = (New-Page -Type 'landing' -Body "Opening paragraph.`n`n## :material-star-four-points-outline: Key Features`n`n### Details`n`nText.`n")
            'start/index.md' = (New-Page -Type 'landing' -Body "Opening paragraph with no sections.`n")
            'cs.md'          = (New-Page -Body "Opening paragraph.`n`n## The order`n`nText.`n")
        }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }
}

Describe 'Test-DocsConformance: LANG-8 no issue or pull request numbers' {
    It 'fails an issue number in running text' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "This was fixed in #242.`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "LANG-8.*'#242'"
    }

    It 'fails a link to an issue' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "See [the issue](https://github.com/TetronIO/JIM/issues/242).`n") }
        $r = Invoke-Conformance -Fixture $f
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'LANG-8'
    }

    It 'passes anchors in link targets, and HTML entities' {
        $f = New-Fixture -Pages @{ 'cs.md' = (New-Page -Body "See [Directory servers](ldap.md#389-directory-server) and [above](#settings)&#8203;.`n") }
        (Invoke-Conformance -Fixture $f).ExitCode | Should -Be 0
    }
}
