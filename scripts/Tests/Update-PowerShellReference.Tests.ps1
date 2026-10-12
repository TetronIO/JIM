# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Update-PowerShellReference.ps1.

.DESCRIPTION
    The script generates each cmdlet's Syntax, Parameters and Output on the docs/powershell/ pages from
    the module itself (#1083), between BEGIN/END GENERATED markers, and -Check fails a pull request whose
    pages and module disagree (ACC-5). The motivating defect: a hand-written Output list named a property
    the object does not have, so an example filtering on it matched every certificate and deleted them all.

    All fixtures (a module, pages, an OpenAPI document and a config) are written under $TestDrive, so none
    of this depends on the live repository's cmdlets or docs. Assertions read the generated tables cell by
    cell rather than comparing text, so a change of layout does not break them.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Update-PowerShellReference.ps1')).Path

    # A Get- cmdlet with two parameter sets, each returning its own API object, and every attribute the
    # generator states: pipeline input, ValidateSet, ValidateRange and SupportsWildcards.
    $script:GetWidget = @'
function Get-JIMWidget {
    <#
    .SYNOPSIS
        Gets Widgets from JIM.

    .PARAMETER Id
        The unique identifier of the Widget.

    .PARAMETER Name
        Filter Widgets by name.

    .PARAMETER Mode
        How much of each Widget to return.

    .PARAMETER PageSize
        Number of Widgets per page.

    .PARAMETER Page
        The page of Widgets to return.

    .OUTPUTS
        A list returns each Widget's header; -Id returns the whole Widget.
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.WidgetHeader', ParameterSetName = 'List')]
    [OutputType('JIM.WidgetDetail', ParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [SupportsWildcards()]
        [string]$Name,

        [Parameter(ParameterSetName = 'List')]
        [ValidateSet('Full', 'Summary')]
        [string]$Mode = 'Full',

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 1000)]
        [int]$PageSize = 100,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1
    )
}
'@

    # A Remove- cmdlet: ShouldProcess (so -WhatIf and -Confirm exist), a switch, and one output type for
    # every parameter set.
    $script:RemoveWidget = @'
function Remove-JIMWidget {
    <#
    .SYNOPSIS
        Removes a Widget.

    .PARAMETER Id
        The unique identifier of the Widget to remove.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .PARAMETER PassThru
        Returns the removed Widget.

    .OUTPUTS
        With -PassThru, the removed Widget; otherwise nothing.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType('JIM.WidgetDetail')]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [int[]]$Id,

        [switch]$Force,

        [switch]$PassThru
    )
}
'@

    # Declares only PSCustomObject, so its output is undescribed.
    $script:GetUndeclared = @'
function Get-JIMUndeclared {
    <#
    .SYNOPSIS
        Returns something undescribed.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param()
}
'@

    # A parameter with no .PARAMETER help.
    $script:GetUndescribed = @'
function Get-JIMUndescribed {
    <#
    .SYNOPSIS
        Has an undescribed parameter.
    #>
    [CmdletBinding()]
    [OutputType('JIM.WidgetHeader')]
    param(
        [string]$Mystery
    )
}
'@

    # Returns a schema that describes none of its properties.
    $script:GetSummary = @'
function Get-JIMWidgetSummary {
    <#
    .SYNOPSIS
        Counts Widgets.
    #>
    [CmdletBinding()]
    [OutputType('JIM.WidgetSummary')]
    param()
}
'@

    # Declares nothing about what it returns.
    $script:GetSilent = @'
function Get-JIMSilent {
    <#
    .SYNOPSIS
        Says nothing about its output.
    #>
    [CmdletBinding()]
    param()
}
'@

    # Declares that it returns nothing.
    $script:InvokeQuiet = @'
function Invoke-JIMQuiet {
    <#
    .SYNOPSIS
        Returns nothing, and says so.

    .OUTPUTS
        None.
    #>
    [CmdletBinding()]
    [OutputType([void])]
    param()
}
'@

    # Builds its own object, so its type is not an OpenAPI schema.
    $script:GetHealth = @'
function Get-JIMHealth {
    <#
    .SYNOPSIS
        Gets JIM's health.

    .OUTPUTS
        A summary with Status and CheckedAt.
    #>
    [CmdletBinding()]
    [OutputType('JIM.HealthSummary')]
    param()
}
'@

    $script:OpenApi = @'
{
  "openapi": "3.1.1",
  "components": {
    "schemas": {
      "WidgetHeader": {
        "type": "object",
        "properties": {
          "id": { "type": "string", "format": "uuid", "description": "The Widget's identifier." },
          "name": { "type": [ "null", "string" ] },
          "isEnabled": { "type": "boolean", "description": "Whether the Widget | is enabled." },
          "status": { "$ref": "#/components/schemas/WidgetStatus" },
          "count": { "pattern": "^-?(?:0|[1-9]\\d*)$", "type": [ "integer", "string" ], "format": "int32" },
          "tags": { "type": "array", "items": { "type": "string" }, "description": "Labels (#12), set by the #789 tagger (Widget tags, #345)." }
        }
      },
      "WidgetStatus": { "enum": [ "Active", "Retired" ] },
      "WidgetSummary": {
        "type": "object",
        "properties": {
          "total": { "type": "integer", "format": "int32" },
          "enabled": { "type": "integer", "format": "int32" }
        }
      },
      "WidgetDetail": {
        "allOf": [ { "$ref": "#/components/schemas/WidgetHeader" } ],
        "type": "object",
        "properties": {
          "notes": { "type": [ "null", "string" ] },
          "createdAt": { "type": "string", "format": "date-time" }
        }
      }
    }
  }
}
'@

    $script:Intro = 'Cmdlets for managing Widgets. Hand-written, and must survive generation.'
    $script:Examples = "### Examples`n`n" + '```powershell title="List Widgets"' + "`nGet-JIMWidget`n" + '```'

    function New-Section {
        param([string]$Cmdlet, [string]$Body = '')
        "## $Cmdlet`n`nHand-written introduction to $Cmdlet.`n`n<!-- BEGIN GENERATED: $Cmdlet -->`n$Body`n<!-- END GENERATED: $Cmdlet -->`n`n$script:Examples`n"
    }

    # Writes a fixture module, pages, OpenAPI document and config under a fresh directory of $TestDrive.
    function New-Fixture {
        param(
            [string[]]$Functions = @($script:GetWidget, $script:RemoveWidget),
            [hashtable]$Pages = @{ 'widgets.md' = "---`ntitle: Widgets`n---`n`n# Widgets`n`n$script:Intro`n`n" + (New-Section 'Get-JIMWidget') + "`n" + (New-Section 'Remove-JIMWidget') },
            [hashtable]$NonApiOutputTypes = @{}
        )

        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $moduleDir = Join-Path $root 'Module'
        $docsDir = Join-Path $root 'docs'
        New-Item -ItemType Directory -Path $moduleDir, $docsDir -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $moduleDir 'Fixture.psm1') -Value ($Functions -join "`n`n")
        New-ModuleManifest -Path (Join-Path $moduleDir 'Fixture.psd1') -RootModule 'Fixture.psm1' -FunctionsToExport '*'

        foreach ($name in $Pages.Keys) {
            Set-Content -LiteralPath (Join-Path $docsDir $name) -Value $Pages[$name] -NoNewline
        }

        $openApiPath = Join-Path $root 'openapi.json'
        Set-Content -LiteralPath $openApiPath -Value $script:OpenApi

        $entries = ($NonApiOutputTypes.Keys | ForEach-Object { "        '$_' = '$($NonApiOutputTypes[$_])'" }) -join "`n"
        $configPath = Join-Path $root 'config.psd1'
        Set-Content -LiteralPath $configPath -Value "@{`n    NonApiOutputTypes = @{`n$entries`n    }`n}"

        [pscustomobject]@{
            Root     = $root
            Manifest = Join-Path $moduleDir 'Fixture.psd1'
            Docs     = $docsDir
            OpenApi  = $openApiPath
            Config   = $configPath
        }
    }

    function Invoke-Generator {
        param($Fixture, [switch]$Check)
        $arguments = @('-NoProfile', '-File', $script:ScriptPath,
            '-ModulePath', $Fixture.Manifest, '-DocsPath', $Fixture.Docs,
            '-OpenApiPath', $Fixture.OpenApi, '-ConfigPath', $Fixture.Config)
        if ($Check) { $arguments += '-Check' }
        $output = & pwsh @arguments 2>&1
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }

    function Get-Page {
        param($Fixture, [string]$Name = 'widgets.md')
        Get-Content -LiteralPath (Join-Path $Fixture.Docs $Name) -Raw
    }

    # The text between a cmdlet's markers.
    function Get-Block {
        param([string]$Page, [string]$Cmdlet)
        $begin = "<!-- BEGIN GENERATED: $Cmdlet -->"
        $end = "<!-- END GENERATED: $Cmdlet -->"
        $start = $Page.IndexOf($begin)
        $stop = $Page.IndexOf($end)
        $Page.Substring($start + $begin.Length, $stop - $start - $begin.Length)
    }

    # The text under one H3 of a block, up to the next H3.
    function Get-Subsection {
        param([string]$Block, [string]$Heading)
        $match = [regex]::Match($Block, '(?ms)^### ' + $Heading + '\s*$(.*?)(?=^### |\z)')
        $match.Groups[1].Value
    }

    # A Markdown table's rows as objects keyed by its header cells, code spans unwrapped.
    function ConvertFrom-MarkdownTable {
        param([string]$Text)
        $lines = @($Text -split "`n" | Where-Object { $_ -match '^\s*\|' })
        if ($lines.Count -lt 2) { return @() }
        $split = {
            param($line)
            $cells = $line.Trim() -replace '^\|', '' -replace '\|$', '' -split '(?<!\\)\|'
            @($cells | ForEach-Object { ($_.Trim() -replace '\\\|', '|') })
        }
        $headers = & $split $lines[0]
        foreach ($line in $lines[2..($lines.Count - 1)]) {
            $cells = & $split $line
            $row = [ordered]@{}
            for ($i = 0; $i -lt $headers.Count; $i++) { $row[$headers[$i]] = $cells[$i] -replace '`', '' }
            [pscustomobject]$row
        }
    }

    # Each type an Output section names: its name, the sets in brackets after it, the rest of its line,
    # and the property table that follows it, if any.
    function Get-OutputTypes {
        param([string]$Block)
        $output = Get-Subsection $Block 'Output'
        $matches = [regex]::Matches($output, '(?m)^\*\*`(?<type>[^`]+)`\*\*(?: \((?<sets>[^)]*)\))?(?<rest>.*)$')
        for ($i = 0; $i -lt $matches.Count; $i++) {
            $m = $matches[$i]
            $end = if ($i + 1 -lt $matches.Count) { $matches[$i + 1].Index } else { $output.Length }
            $after = $output.Substring($m.Index + $m.Length, $end - $m.Index - $m.Length)
            [pscustomobject]@{
                Type  = $m.Groups['type'].Value
                Sets  = $m.Groups['sets'].Value
                Rest  = $m.Groups['rest'].Value
                Table = @(ConvertFrom-MarkdownTable $after)
            }
        }
    }

    function Get-SyntaxLines {
        param([string]$Block)
        $syntax = Get-Subsection $Block 'Syntax'
        @(($syntax -split "`n") | Where-Object { $_.Trim() -and $_ -notmatch '^```' })
    }
}

Describe 'Update-PowerShellReference: generating a block' {
    BeforeAll {
        $script:Fixture = New-Fixture
        $script:Run = Invoke-Generator $script:Fixture
        $script:Page = Get-Page $script:Fixture
        $script:GetBlock = Get-Block $script:Page 'Get-JIMWidget'
        $script:RemoveBlock = Get-Block $script:Page 'Remove-JIMWidget'
    }

    It 'succeeds' {
        $script:Run.ExitCode | Should -Be 0 -Because $script:Run.Output
    }

    It 'gives one syntax line per parameter set, labelling the default set' {
        $lines = Get-SyntaxLines $script:GetBlock
        ($lines | Where-Object { $_ -match '^#' }) | Should -Be @('# List (default)', '# ById')
        $byId = $lines[[array]::IndexOf($lines, '# ById') + 1]
        $byId | Should -Match '^Get-JIMWidget -Id <guid>$'
    }

    It 'writes mandatory parameters bare and optional ones in brackets' {
        $lines = Get-SyntaxLines $script:GetBlock
        $list = $lines[[array]::IndexOf($lines, '# List (default)') + 1]
        $list | Should -Match '\[-Name <string>\]'
        $list | Should -Match '\[-PageSize <int>\]'
        $list | Should -Not -Match '-Id'
        (Get-SyntaxLines $script:RemoveBlock) | Should -Contain 'Remove-JIMWidget -Id <int[]> [-Force] [-PassThru]'
    }

    It 'leaves out the common parameters, -WhatIf and -Confirm' {
        $script:RemoveBlock | Should -Not -MatchExactly '-(WhatIf|Confirm|Verbose|ErrorAction)\b|`(WhatIf|Confirm|Verbose|ErrorAction)`'
    }

    It 'lists each parameter with its type, where it is required, its default and its help' {
        $rows = ConvertFrom-MarkdownTable (Get-Subsection $script:GetBlock 'Parameters')
        $rows.Name | Should -Be @('Id', 'Name', 'Mode', 'PageSize', 'Page')

        $id = $rows | Where-Object Name -eq 'Id'
        $id.Type | Should -Be 'guid'
        $id.Required | Should -Be 'Yes (ById)'
        $id.Description | Should -Match '^The unique identifier of the Widget\.'

        $pageSize = $rows | Where-Object Name -eq 'PageSize'
        $pageSize.Required | Should -Be 'No (List)'
        $pageSize.Default | Should -Be '100'

        ($rows | Where-Object Name -eq 'Mode').Default | Should -Be "'Full'"
    }

    It 'gives a switch no default, and a parameter in every set no set names' {
        $rows = ConvertFrom-MarkdownTable (Get-Subsection $script:RemoveBlock 'Parameters')
        ($rows | Where-Object Name -eq 'Force').Default | Should -BeNullOrEmpty
        ($rows | Where-Object Name -eq 'Force').Type | Should -Be 'switch'
        ($rows | Where-Object Name -eq 'Id').Required | Should -Be 'Yes'
    }

    It 'states pipeline input, a ValidateSet, a ValidateRange and wildcard support' {
        $rows = ConvertFrom-MarkdownTable (Get-Subsection $script:GetBlock 'Parameters')
        ($rows | Where-Object Name -eq 'Id').Description | Should -Match 'pipeline input by property name'
        ($rows | Where-Object Name -eq 'Mode').Description | Should -Match 'Full.*Summary'
        ($rows | Where-Object Name -eq 'PageSize').Description | Should -Match '1 to 1,000'
        ($rows | Where-Object Name -eq 'Page').Description | Should -Match 'At least 1\.'
        ($rows | Where-Object Name -eq 'Name').Description | Should -Match 'wildcard'

        $remove = ConvertFrom-MarkdownTable (Get-Subsection $script:RemoveBlock 'Parameters')
        ($remove | Where-Object Name -eq 'Id').Description | Should -Match 'pipeline input'
        ($remove | Where-Object Name -eq 'Id').Description | Should -Not -Match 'by property name'
    }

    It 'gives the .OUTPUTS help as prose' {
        Get-Subsection $script:GetBlock 'Output' | Should -Match "A list returns each Widget's header; -Id returns the whole Widget\."
    }

    It 'tables each output type with the sets that return it, and its properties in PascalCase with their types' {
        $header = Get-OutputTypes $script:GetBlock | Where-Object Type -eq 'WidgetHeader'
        $header.Sets | Should -Be 'List'
        $header.Table.Property | Should -Be @('Id', 'Name', 'IsEnabled', 'Status', 'Count', 'Tags')
        $header.Table.Type | Should -Be @('guid', 'string', 'bool', 'WidgetStatus', 'int', 'string[]')
    }

    It "gives each property the description the schema has for it, and none where it has none" {
        $table = (Get-OutputTypes $script:GetBlock | Where-Object Type -eq 'WidgetHeader').Table
        ($table | Where-Object Property -eq 'Id').Description | Should -Be "The Widget's identifier."
        ($table | Where-Object Property -eq 'IsEnabled').Description | Should -Be 'Whether the Widget | is enabled.'
        ($table | Where-Object Property -eq 'Name').Description | Should -BeNullOrEmpty
    }

    It "leaves out the issue numbers a schema's description cites, since a public page cannot carry them" {
        $table = (Get-OutputTypes $script:GetBlock | Where-Object Type -eq 'WidgetHeader').Table
        ($table | Where-Object Property -eq 'Tags').Description | Should -Be 'Labels, set by the tagger (Widget tags).'
    }

    It 'includes the properties a schema takes from another through allOf' {
        $detail = Get-OutputTypes $script:GetBlock | Where-Object Type -eq 'WidgetDetail'
        $detail.Sets | Should -Be 'ById'
        $detail.Table.Property | Should -Be @('Id', 'Name', 'IsEnabled', 'Status', 'Count', 'Tags', 'Notes', 'CreatedAt')
    }

    It 'refers back to the cmdlet above that already tables a type, instead of repeating it' {
        $detail = @(Get-OutputTypes $script:RemoveBlock)
        $detail.Count | Should -Be 1
        $detail[0].Type | Should -Be 'WidgetDetail'
        $detail[0].Table.Count | Should -Be 0
        $detail[0].Rest | Should -Match ([regex]::Escape('[Get-JIMWidget](#get-jimwidget)'))
    }

    It 'tables a type without a Description column when its schema describes none of its properties' {
        $fixture = New-Fixture -Functions @($script:GetSummary) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMWidgetSummary') }
        Invoke-Generator $fixture | Out-Null
        $summary = @(Get-OutputTypes (Get-Block (Get-Page $fixture) 'Get-JIMWidgetSummary'))
        $summary[0].Table.Property | Should -Be @('Total', 'Enabled')
        $summary[0].Table[0].PSObject.Properties.Name | Should -Not -Contain 'Description'
    }

    It 'names no sets for a type every set returns' {
        $fixture = New-Fixture -Functions @($script:RemoveWidget) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Remove-JIMWidget') }
        Invoke-Generator $fixture | Out-Null
        $detail = @(Get-OutputTypes (Get-Block (Get-Page $fixture) 'Remove-JIMWidget'))
        $detail[0].Sets | Should -BeNullOrEmpty
        $detail[0].Table.Property | Should -Contain 'CreatedAt'
    }

    It 'leaves everything outside the markers as it was' {
        $script:Page | Should -Match ([regex]::Escape("# Widgets`n`n$script:Intro"))
        $script:Page | Should -Match ([regex]::Escape('Hand-written introduction to Get-JIMWidget.'))
        ([regex]::Matches($script:Page, [regex]::Escape($script:Examples))).Count | Should -Be 2
    }

    It 'changes nothing when run again' {
        Invoke-Generator $script:Fixture | Out-Null
        Get-Page $script:Fixture | Should -BeExactly $script:Page
    }
}

Describe 'Update-PowerShellReference -Check' {
    It 'passes when every block matches the module' {
        $fixture = New-Fixture
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
    }

    It 'fails when a block was edited by hand, naming the page and the cmdlet, and writes nothing' {
        $fixture = New-Fixture
        Invoke-Generator $fixture | Out-Null
        $path = Join-Path $fixture.Docs 'widgets.md'
        $edited = (Get-Content -LiteralPath $path -Raw) -replace 'Number of Widgets per page\.', 'Number of Widgets on each page.'
        Set-Content -LiteralPath $path -Value $edited -NoNewline

        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'ACC-5'
        $result.Output | Should -Match 'widgets\.md'
        $result.Output | Should -Match 'Get-JIMWidget'
        Get-Content -LiteralPath $path -Raw | Should -BeExactly $edited
    }

    It 'writes, and checks, a change that differs only in case' {
        $fixture = New-Fixture
        Invoke-Generator $fixture | Out-Null
        $path = Join-Path $fixture.Docs 'widgets.md'
        $lowered = (Get-Content -LiteralPath $path -Raw) -creplace 'Number of Widgets per page\.', 'number of widgets per page.'
        Set-Content -LiteralPath $path -Value $lowered -NoNewline

        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0 -Because 'a block differing only in case is drift'

        Invoke-Generator $fixture | Out-Null
        Get-Content -LiteralPath $path -Raw | Should -MatchExactly 'Number of Widgets per page\.'
    }

    It 'fails when a block names a cmdlet the module does not export' {
        $fixture = New-Fixture -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMGone') }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Get-JIMGone'
    }

    It 'fails when a parameter has no .PARAMETER help' {
        $fixture = New-Fixture -Functions @($script:GetUndescribed) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMUndescribed') }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Mystery'
    }

    It 'fails when a cmdlet declares no output type but PSCustomObject' {
        $fixture = New-Fixture -Functions @($script:GetUndeclared) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMUndeclared') }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Get-JIMUndeclared'
        $result.Output | Should -Match 'OutputType'
    }

    It 'fails when a cmdlet declares nothing about its output' {
        $fixture = New-Fixture -Functions @($script:GetSilent) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMSilent') }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Get-JIMSilent'
        $result.Output | Should -Match 'OutputType\(\[void\]\)'
    }

    It 'accepts a cmdlet that declares it returns nothing' {
        $fixture = New-Fixture -Functions @($script:InvokeQuiet) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Invoke-JIMQuiet') }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Subsection (Get-Block (Get-Page $fixture) 'Invoke-JIMQuiet') 'Output' | Should -Match 'None\.'
    }

    It 'fails when a JIM output type is neither an OpenAPI schema nor allowed' {
        $fixture = New-Fixture -Functions @($script:GetHealth) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMHealth') }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'JIM\.HealthSummary'
    }

    It 'accepts an allowed output type that is not an OpenAPI schema, and lists it by name' {
        $fixture = New-Fixture -Functions @($script:GetHealth) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMHealth') } -NonApiOutputTypes @{ 'JIM.HealthSummary' = 'Built by the cmdlet from several API calls' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $types = @(Get-OutputTypes (Get-Block (Get-Page $fixture) 'Get-JIMHealth'))
        $types[0].Type | Should -Be 'HealthSummary'
        $types[0].Table.Count | Should -Be 0
    }

    It 'fails on an allowed output type that no cmdlet declares' {
        $fixture = New-Fixture -NonApiOutputTypes @{ 'JIM.Stale' = 'Left behind' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'JIM\.Stale'
    }

    It 'fails on a BEGIN marker with no END' {
        $fixture = New-Fixture -Pages @{ 'widgets.md' = "# Widgets`n`n## Get-JIMWidget`n`n<!-- BEGIN GENERATED: Get-JIMWidget -->`n" }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'END'
    }

    It 'warns about a cmdlet section with no block, without failing' {
        $page = "# Widgets`n`n" + (New-Section 'Get-JIMWidget') + "`n## Remove-JIMWidget`n`nHand-written, not generated yet.`n"
        $fixture = New-Fixture -Pages @{ 'widgets.md' = $page }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match 'Remove-JIMWidget'
        Get-Page $fixture | Should -Match 'Hand-written, not generated yet\.'
    }

    It 'warns about every exported cmdlet with no block, whether or not it has a section, whatever its name' {
        # Connect-JIM has nothing after "JIM", which a heading pattern can miss; Remove-JIMWidget is only
        # mentioned in a table, so there is no heading to find at all.
        $connect = @'
function Connect-JIM {
    <#
    .SYNOPSIS
        Connects.
    #>
    [CmdletBinding()]
    [OutputType([void])]
    param()
}
'@
        $page = "# Widgets`n`n" + (New-Section 'Get-JIMWidget') + "`n## Connect-JIM`n`nHand-written.`n`n| Cmdlet | Purpose |`n|---|---|`n| ``Remove-JIMWidget -Id <int>`` | Removes one. |`n"
        $fixture = New-Fixture -Functions @($script:GetWidget, $script:RemoveWidget, $connect) -Pages @{ 'widgets.md' = $page }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match 'widgets\.md: Connect-JIM\b'
        $result.Output | Should -Match '\(no section\): Remove-JIMWidget'
        $result.Output | Should -Match '2 cmdlets have no generated block'
        $result.Output | Should -Not -Match ': Get-JIMWidget'
    }
}
