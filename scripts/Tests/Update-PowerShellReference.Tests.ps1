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
        A summary of JIM's health.

        JIM.HealthSummary
        Status (string): Healthy or Unhealthy.
        CheckedAt (datetime): When the check ran, which
            is UTC.
    #>
    [CmdletBinding()]
    [OutputType('JIM.HealthSummary')]
    param()
}
'@

    # Builds its own object but lists none of its properties.
    $script:GetHealthUnlisted = @'
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

    # Gives the API's WidgetHeader a type name of its own, so the name is not a schema but the properties are.
    $script:GetGadget = @'
function Get-JIMGadget {
    <#
    .SYNOPSIS
        Gets Gadgets, which are Widgets under the module's own name.
    #>
    [CmdletBinding()]
    [OutputType('JIM.Gadget')]
    param()
}
'@

    # Declares positions, so they are part of its interface: a mandatory one, an optional one, and a switch.
    $script:SetWidgetName = @'
function Set-JIMWidgetName {
    <#
    .SYNOPSIS
        Renames a Widget.

    .PARAMETER Id
        The Widget to rename.

    .PARAMETER Name
        The new name.

    .PARAMETER PassThru
        Returns the renamed Widget.
    #>
    [CmdletBinding()]
    [OutputType('JIM.WidgetHeader')]
    param(
        [Parameter(Mandatory, Position = 0)]
        [int]$Id,

        [Parameter(Position = 1)]
        [string]$Name,

        [switch]$PassThru
    )
}
'@

    # A child-noun cmdlet that takes its parent from the pipeline: a piped Widget carries Id, not WidgetId, so
    # the alias is what binds it. And an alias kept only so an old spelling still works.
    $script:GetWidgetPart = @'
function Get-JIMWidgetPart {
    <#
    .SYNOPSIS
        Gets a Widget's parts.

    .PARAMETER WidgetId
        The Widget whose parts to get.

    .PARAMETER Kind
        The kind of part.
    #>
    [CmdletBinding()]
    [OutputType('JIM.WidgetHeader')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$WidgetId,

        [Alias('PartKind')]
        [string]$Kind
    )
}
'@

    # Help written for Get-Help, which renders no Markdown: names a cmdlet, its parameters and PowerShell's
    # constants in plain text, and says "null", which the page must not (VOI-1).
    $script:GetWidgetNote = @'
function Get-JIMWidgetNote {
    <#
    .SYNOPSIS
        Gets a Widget's note.

    .PARAMETER Kind
        Pipe it from Get-JIMWidget -Name "x", or give it with -Kind; $true keeps it. Null when the Widget has none,
        which `Clear-Thing -Now` sets, as $null does. Its "Null is a value" setting is a label, and stays as written.

    .OUTPUTS
        Each note, or null where there is none; see Get-JIMWidget -Id.
    #>
    [CmdletBinding()]
    [OutputType([void])]
    param([string]$Kind)
}
'@

    # Declares no position, so PowerShell gives its parameter one nobody chose.
    $script:GetWidgetCount = @'
function Get-JIMWidgetCount {
    <#
    .SYNOPSIS
        Counts a Widget's parts.

    .PARAMETER Id
        The Widget.
    #>
    [CmdletBinding()]
    [OutputType('JIM.WidgetSummary')]
    param(
        [Parameter(Mandatory)]
        [int]$Id
    )
}
'@

    # Returns a schema whose descriptions carry the text .NET renders for a <see cref> to a member.
    $script:GetReport = @'
function Get-JIMWidgetReport {
    <#
    .SYNOPSIS
        Reports on Widgets.
    #>
    [CmdletBinding()]
    [OutputType('JIM.WidgetReport')]
    param()
}
'@

    # Every set needs -Id, so -Causes is what tells the second apart.
    $script:GetWidgetEvent = @'
function Get-JIMWidgetEvent {
    <#
    .SYNOPSIS
        Gets a Widget's events.

    .PARAMETER Id
        The Widget.

    .PARAMETER Causes
        Returns what caused the events instead.

    .OUTPUTS
        The events, or with -Causes what caused them.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Events')]
    [OutputType('JIM.WidgetHeader', ParameterSetName = 'Events')]
    [OutputType('JIM.WidgetSummary', ParameterSetName = 'Causes')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Events')]
        [Parameter(Mandatory, ParameterSetName = 'Causes')]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'Causes')]
        [switch]$Causes
    )
}
'@

    # Two sets return the same object; the second also needs the first's -Id.
    $script:GetWidgetFeed = @'
function Get-JIMWidgetFeed {
    <#
    .SYNOPSIS
        Gets Widgets, or follows one.

    .PARAMETER Id
        The Widget.

    .PARAMETER Follow
        Follows the Widget until it settles.

    .PARAMETER Name
        Filter Widgets by name.

    .OUTPUTS
        Each Widget's header, or one Widget in full.
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.WidgetHeader', ParameterSetName = 'List')]
    [OutputType('JIM.WidgetDetail', ParameterSetName = 'ById')]
    [OutputType('JIM.WidgetDetail', ParameterSetName = 'Follow')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [Parameter(Mandatory, ParameterSetName = 'Follow')]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'Follow')]
        [switch]$Follow,

        [Parameter(ParameterSetName = 'List')]
        [string]$Name
    )
}
'@

    # Returns one of two things from its only parameter set, so the set cannot say which; the help labels each.
    $script:TestWidget = @'
function Test-JIMWidget {
    <#
    .SYNOPSIS
        Tests JIM's Widgets.

    .PARAMETER Quiet
        Returns only whether every Widget passed.

    .OUTPUTS
        The result, or with -Quiet whether every Widget passed.

        JIM.HealthSummary: By default
        Status (string): Healthy or Unhealthy.

        System.Boolean: With -Quiet
    #>
    [CmdletBinding()]
    [OutputType('JIM.HealthSummary')]
    [OutputType([bool])]
    param([switch]$Quiet)
}
'@

    # Returns a primitive, and only that.
    $script:TestWidgetName = @'
function Test-JIMWidgetName {
    <#
    .SYNOPSIS
        Tests whether a Widget name is free.

    .PARAMETER Name
        The name.

    .OUTPUTS
        $true if the name is free; otherwise $false.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([string]$Name)
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
      "WidgetStatus": { "enum": [ "Active", "Retired", null ] },
      "WidgetSummary": {
        "type": "object",
        "properties": {
          "total": { "type": "integer", "format": "int32" },
          "enabled": { "type": "integer", "format": "int32" }
        }
      },
      "WidgetReport": {
        "type": "object",
        "properties": {
          "worst": { "type": "string", "description": "The worst status among List&lt;WidgetHeader&gt; WidgetReport.Widgets; The WidgetReport.Widgets list stays." },
          "limit": { "type": "integer", "format": "int32", "description": "Never ordered: the sentinel (int int.MaxValue), unlike ServiceHealthStatus WidgetReport.Worst." },
          "culture": { "type": "string", "description": "The .NET culture code (#12), for example en-GB." }
        }
      },
      "WidgetDetail": {
        "allOf": [ { "$ref": "#/components/schemas/WidgetHeader" } ],
        "type": "object",
        "properties": {
          "notes": { "type": [ "null", "string" ] },
          "createdAt": { "type": "string", "format": "date-time" },
          "replacedBy": { "oneOf": [ { "type": "null" }, { "description": "The Widget that replaced this one.", "$ref": "#/components/schemas/WidgetHeader" } ] },
          "parts": { "type": "array", "items": { "$ref": "#/components/schemas/WidgetSummary" }, "description": "The Widget's parts." },
          "history": { "type": "array", "items": { "$ref": "#/components/schemas/WidgetStatus" } }
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
            [hashtable]$NonApiOutputTypes = @{},
            [hashtable]$OutputTypeSchemas = @{}
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
        $mappings = ($OutputTypeSchemas.Keys | ForEach-Object { "        '$_' = '$($OutputTypeSchemas[$_])'" }) -join "`n"
        $configPath = Join-Path $root 'config.psd1'
        Set-Content -LiteralPath $configPath -Value "@{`n    NonApiOutputTypes = @{`n$entries`n    }`n    OutputTypeSchemas = @{`n$mappings`n    }`n}"

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

    # Each section of an Output: its label (the bold text before the colon, code spans unwrapped), the rest of
    # its line, and the table that follows it, if any. A table no label precedes is a section with no label.
    function Get-OutputSections {
        param([string]$Block)
        $output = Get-Subsection $Block 'Output'
        $labels = @([regex]::Matches($output, '(?m)^\*\*(?<label>.+?)\*\*:(?<rest>.*)$'))
        $leadEnd = if ($labels.Count -gt 0) { $labels[0].Index } else { $output.Length }
        $lead = @(ConvertFrom-MarkdownTable $output.Substring(0, $leadEnd))
        if ($lead.Count -gt 0) { [pscustomobject]@{ Label = ''; Rest = ''; Table = $lead } }
        for ($i = 0; $i -lt $labels.Count; $i++) {
            $m = $labels[$i]
            $end = if ($i + 1 -lt $labels.Count) { $labels[$i + 1].Index } else { $output.Length }
            $after = $output.Substring($m.Index + $m.Length, $end - $m.Index - $m.Length)
            [pscustomobject]@{
                Label = $m.Groups['label'].Value -replace '`', ''
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
        (Get-SyntaxLines $script:RemoveBlock) | Should -Contain 'Remove-JIMWidget -Id <int[]> [-Force] [-PassThru] [-WhatIf] [-Confirm]'
    }

    It 'leaves out the common parameters' {
        $script:RemoveBlock | Should -Not -MatchExactly '-(Verbose|ErrorAction)\b|`(Verbose|ErrorAction|WhatIf|Confirm)`'
    }

    It 'ends the syntax of a cmdlet that supports ShouldProcess with -WhatIf and -Confirm, and lists neither as a parameter' {
        Get-SyntaxLines $script:RemoveBlock | Should -Be @('Remove-JIMWidget -Id <int[]> [-Force] [-PassThru] [-WhatIf] [-Confirm]')
        Get-SyntaxLines $script:GetBlock | Should -Not -Match 'WhatIf|Confirm'
    }

    It 'says a cmdlet whose confirmation impact is High asks before it acts, and no other' {
        $remove = @'
function Remove-JIMGadget {
    <#
    .SYNOPSIS
        Removes a Gadget.

    .PARAMETER Id
        The Gadget to remove.
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([void])]
    param(
        [Parameter(Mandatory)]
        [int]$Id
    )
}
'@
        $fixture = New-Fixture -Functions @($remove) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Remove-JIMGadget') }
        Invoke-Generator $fixture | Out-Null
        $syntax = Get-Subsection (Get-Block (Get-Page $fixture) 'Remove-JIMGadget') 'Syntax'
        $syntax | Should -Match 'asks for confirmation before it acts'
        $syntax | Should -Not -Match '-Force'
        Get-Subsection $script:RemoveBlock 'Syntax' | Should -Not -Match 'asks for confirmation'
    }

    It 'names -Force as a way past the prompt where the cmdlet has one' {
        $remove = @'
function Remove-JIMGadget {
    <#
    .SYNOPSIS
        Removes a Gadget.

    .PARAMETER Id
        The Gadget to remove.

    .PARAMETER Force
        Skips the confirmation prompt.
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([void])]
    param(
        [Parameter(Mandatory)]
        [int]$Id,

        [switch]$Force
    )
}
'@
        $fixture = New-Fixture -Functions @($remove) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Remove-JIMGadget') }
        Invoke-Generator $fixture | Out-Null
        Get-Subsection (Get-Block (Get-Page $fixture) 'Remove-JIMGadget') 'Syntax' | Should -Match '`-Force` or `-Confirm:\$false` skips the prompt'
    }

    It 'labels a parameter set named Default once' {
        $disconnect = @'
function Disconnect-JIMWidget {
    <#
    .SYNOPSIS
        Disconnects.

    .PARAMETER Url
        Where from.

    .PARAMETER All
        From everywhere.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Default')]
    [OutputType([void])]
    param(
        [Parameter(ParameterSetName = 'Default')]
        [string]$Url,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All
    )
}
'@
        $fixture = New-Fixture -Functions @($disconnect) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Disconnect-JIMWidget') }
        Invoke-Generator $fixture | Out-Null
        (Get-SyntaxLines (Get-Block (Get-Page $fixture) 'Disconnect-JIMWidget') | Where-Object { $_ -match '^#' }) | Should -Be @('# Default', '# All')
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

    It 'names the properties a parameter binds from the pipeline, and an alias that does not' {
        $fixture = New-Fixture -Functions @($script:GetWidgetPart) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMWidgetPart') }
        Invoke-Generator $fixture | Out-Null
        $rows = ConvertFrom-MarkdownTable (Get-Subsection (Get-Block (Get-Page $fixture) 'Get-JIMWidgetPart') 'Parameters')
        ($rows | Where-Object Name -eq 'WidgetId').Description | Should -Match 'pipeline input by property name, WidgetId or Id\.'
        ($rows | Where-Object Name -eq 'Kind').Description | Should -Match 'Also accepted as -PartKind\.'
    }

    It 'puts cmdlets, parameters and PowerShell''s constants in code, and writes empty for null' {
        $fixture = New-Fixture -Functions @($script:GetWidgetNote) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMWidgetNote') }
        Invoke-Generator $fixture | Out-Null
        $block = Get-Block (Get-Page $fixture) 'Get-JIMWidgetNote'
        $block | Should -Match 'Pipe it from `Get-JIMWidget -Name` "x", or give it with `-Kind`; `\$true` keeps it\.'
        $block | Should -Match 'Empty when the Widget has none, which `Clear-Thing -Now` sets, as `\$null` does\.'
        $block | Should -Match 'Each note, or empty where there is none; see `Get-JIMWidget -Id`\.'
        $block | Should -Match 'Its "Null is a value" setting is a label' -Because 'a quoted label is the product''s own wording'
        $block | Should -Not -Match '(?<!`)``(?!`)' -Because 'text already in code is left as it is'
    }

    It 'gives the .OUTPUTS help as prose' {
        Get-Subsection $script:GetBlock 'Output' | Should -Match "A list returns each Widget's header; ``-Id`` returns the whole Widget\."
    }

    It 'labels each output by the parameters that return it, never by its class name, and tables its properties in PascalCase with their types' {
        $sections = @(Get-OutputSections $script:GetBlock)
        $sections.Label | Should -Be @('By default', 'With -Id')
        $sections[0].Table.Property | Should -Be @('Id', 'Name', 'IsEnabled', 'Status', 'Count', 'Tags')
        $sections[0].Table.Type | Should -Be @('guid', 'string', 'bool', 'string', 'int', 'string[]')
        Get-Subsection $script:GetBlock 'Output' | Should -Not -Match 'WidgetHeader|WidgetDetail|WidgetStatus'
    }

    It 'shows an enumeration as a string, naming its values' {
        $status = (Get-OutputSections $script:GetBlock)[0].Table | Where-Object Property -eq 'Status'
        $status.Type | Should -Be 'string'
        $status.Description | Should -Be 'One of Active or Retired.'
        $history = (Get-OutputSections $script:GetBlock)[1].Table | Where-Object Property -eq 'History'
        $history.Type | Should -Be 'string[]'
        $history.Description | Should -Be 'Each is one of Active or Retired.'
    }

    It 'shows a nested object as an object, naming its properties' {
        $table = (Get-OutputSections $script:GetBlock)[1].Table
        $parts = $table | Where-Object Property -eq 'Parts'
        $parts.Type | Should -Be 'object[]'
        $parts.Description | Should -Be "The Widget's parts. Each has Total and Enabled."
    }

    It "gives each property the description the schema has for it, and none where it has none" {
        $table = (Get-OutputSections $script:GetBlock)[0].Table
        ($table | Where-Object Property -eq 'Id').Description | Should -Be "The Widget's identifier."
        ($table | Where-Object Property -eq 'IsEnabled').Description | Should -Be 'Whether the Widget | is enabled.'
        ($table | Where-Object Property -eq 'Name').Description | Should -BeNullOrEmpty
    }

    It "leaves out the issue numbers a schema's description cites, since a public page cannot carry them" {
        $table = (Get-OutputSections $script:GetBlock)[0].Table
        ($table | Where-Object Property -eq 'Tags').Description | Should -Be 'Labels, set by the tagger (Widget tags).'
    }

    It 'includes the properties a schema takes from another through allOf' {
        $detail = (Get-OutputSections $script:GetBlock)[1]
        $detail.Label | Should -Be 'With -Id'
        $detail.Table.Property | Should -Be @('Id', 'Name', 'IsEnabled', 'Status', 'Count', 'Tags', 'Notes', 'CreatedAt', 'ReplacedBy', 'Parts', 'History')
    }

    It 'gives a nullable reference the description the document puts beside the reference, not on the property' {
        $replacedBy = (Get-OutputSections $script:GetBlock)[1].Table | Where-Object Property -eq 'ReplacedBy'
        $replacedBy.Type | Should -Be 'object'
        $replacedBy.Description | Should -Be 'The Widget that replaced this one. It has Id, Name, IsEnabled, Status, Count and Tags.'
    }

    It 'refers back to the cmdlet above that already tables a type, instead of repeating it' {
        $output = Get-Subsection $script:RemoveBlock 'Output'
        @(Get-OutputSections $script:RemoveBlock).Count | Should -Be 0
        $output | Should -Match ([regex]::Escape('Properties as under [Get-JIMWidget](#get-jimwidget).'))
        $output | Should -Not -Match 'WidgetDetail'

    }

    It "names a member a description cross-references, without the type and owner .NET renders with it" {
        $fixture = New-Fixture -Functions @($script:GetReport) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMWidgetReport') }
        Invoke-Generator $fixture | Out-Null
        $table = (Get-OutputSections (Get-Block (Get-Page $fixture) 'Get-JIMWidgetReport'))[0].Table
        ($table | Where-Object Property -eq 'Worst').Description | Should -Be 'The worst status among Widgets; The WidgetReport.Widgets list stays.'
        ($table | Where-Object Property -eq 'Limit').Description | Should -Be 'Never ordered: the sentinel (MaxValue), unlike Worst.'
        ($table | Where-Object Property -eq 'Culture').Description | Should -Be 'The .NET culture code, for example en-GB.' -Because 'only a gap a removed issue number leaves before punctuation closes, not the space before .NET'
    }

    It 'shows the positions a cmdlet declares, in the syntax and in the parameter table' {
        $fixture = New-Fixture -Functions @($script:SetWidgetName) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Set-JIMWidgetName') }
        Invoke-Generator $fixture | Out-Null
        $block = Get-Block (Get-Page $fixture) 'Set-JIMWidgetName'
        Get-SyntaxLines $block | Should -Be @('Set-JIMWidgetName [-Id] <int> [[-Name] <string>] [-PassThru]')
        $rows = ConvertFrom-MarkdownTable (Get-Subsection $block 'Parameters')
        ($rows | Where-Object Name -eq 'Id').Description | Should -Be 'The Widget to rename. Can be given by position, as the first argument.'
        ($rows | Where-Object Name -eq 'Name').Description | Should -Be 'The new name. Can be given by position, as the second argument.'
        ($rows | Where-Object Name -eq 'PassThru').Description | Should -Be 'Returns the renamed Widget.'
    }

    It 'says nothing of a position the cmdlet did not declare' {
        $fixture = New-Fixture -Functions @($script:GetWidgetCount) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMWidgetCount') }
        Invoke-Generator $fixture | Out-Null
        $block = Get-Block (Get-Page $fixture) 'Get-JIMWidgetCount'
        Get-SyntaxLines $block | Should -Be @('Get-JIMWidgetCount -Id <int>')
        (ConvertFrom-MarkdownTable (Get-Subsection $block 'Parameters') | Where-Object Name -eq 'Id').Description | Should -Be 'The Widget.'
    }

    It 'tables a type without a Description column when its schema describes none of its properties' {
        $fixture = New-Fixture -Functions @($script:GetSummary) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMWidgetSummary') }
        Invoke-Generator $fixture | Out-Null
        $summary = @(Get-OutputSections (Get-Block (Get-Page $fixture) 'Get-JIMWidgetSummary'))
        $summary[0].Table.Property | Should -Be @('Total', 'Enabled')
        $summary[0].Table[0].PSObject.Properties.Name | Should -Not -Contain 'Description'
    }

    It 'gives no label to the only thing a cmdlet returns, when every set returns it' {
        $fixture = New-Fixture -Functions @($script:RemoveWidget) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Remove-JIMWidget') }
        Invoke-Generator $fixture | Out-Null
        $detail = @(Get-OutputSections (Get-Block (Get-Page $fixture) 'Remove-JIMWidget'))
        $detail.Count | Should -Be 1
        $detail[0].Label | Should -Be ''
        $detail[0].Table.Property | Should -Contain 'CreatedAt'
    }

    It 'names the parameter that tells a set apart, not one every set needs' {
        $fixture = New-Fixture -Functions @($script:GetWidgetEvent) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMWidgetEvent') }
        Invoke-Generator $fixture | Out-Null
        @(Get-OutputSections (Get-Block (Get-Page $fixture) 'Get-JIMWidgetEvent')).Label | Should -Be @('By default', 'With -Causes')
    }

    It 'names every set that returns a type, by what sets each apart' {
        $fixture = New-Fixture -Functions @($script:GetWidgetFeed) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMWidgetFeed') }
        Invoke-Generator $fixture | Out-Null
        @(Get-OutputSections (Get-Block (Get-Page $fixture) 'Get-JIMWidgetFeed')).Label | Should -Be @('By default', 'With -Id or -Follow')
    }

    It 'says what a primitive output is in a sentence' {
        $fixture = New-Fixture -Functions @($script:TestWidgetName) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Test-JIMWidgetName') }
        Invoke-Generator $fixture | Out-Null
        $output = Get-Subsection (Get-Block (Get-Page $fixture) 'Test-JIMWidgetName') 'Output'
        $output | Should -Match ([regex]::Escape('Returns a `bool`.'))
        $output | Should -Not -Match '\*\*'
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

    It 'tables a type the module builds from the properties its .OUTPUTS help lists, and keeps the list out of the prose' {
        $fixture = New-Fixture -Functions @($script:GetHealth) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMHealth') } -NonApiOutputTypes @{ 'JIM.HealthSummary' = 'Built by the cmdlet from several API calls' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $block = Get-Block (Get-Page $fixture) 'Get-JIMHealth'
        $types = @(Get-OutputSections $block)
        $types[0].Label | Should -Be ''
        @($types[0].Table | ForEach-Object Property) | Should -Be @('Status', 'CheckedAt')
        ($types[0].Table | Where-Object Property -eq 'CheckedAt').Type | Should -Be 'datetime'
        ($types[0].Table | Where-Object Property -eq 'CheckedAt').Description | Should -Be 'When the check ran, which is UTC.'
        Get-Subsection $block 'Output' | Should -Match "A summary of JIM's health\."
        Get-Subsection $block 'Output' | Should -Not -Match 'JIM\.HealthSummary|Status \(string\)'
    }

    It 'resolves a listed property type that names a schema as the API''s own are, so no class name reaches the page' {
        $lines = '${1}Status (WidgetStatus): How the Widgets stand.', '${1}Worst (WidgetHeader): The worst Widget.', '${1}Each (WidgetStatus[]): Every Widget''s status.'
        $typed = $script:GetHealth -replace '(?m)^( *)Status \(string\): Healthy or Unhealthy\.', ($lines -join "`n")
        $fixture = New-Fixture -Functions @($typed) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMHealth') } -NonApiOutputTypes @{ 'JIM.HealthSummary' = 'Built by the cmdlet from several API calls' }
        Invoke-Generator $fixture | Out-Null
        $table = (Get-OutputSections (Get-Block (Get-Page $fixture) 'Get-JIMHealth'))[0].Table
        ($table | Where-Object Property -eq 'Status').Type | Should -Be 'string'
        ($table | Where-Object Property -eq 'Status').Description | Should -Be 'How the Widgets stand. One of Active or Retired.'
        ($table | Where-Object Property -eq 'Worst').Type | Should -Be 'object'
        ($table | Where-Object Property -eq 'Worst').Description | Should -Be 'The worst Widget. It has Id, Name, IsEnabled, Status, Count and Tags.'
        ($table | Where-Object Property -eq 'Each').Type | Should -Be 'string[]'
        ($table | Where-Object Property -eq 'Each').Description | Should -Be "Every Widget's status. Each is one of Active or Retired."
    }

    It 'fails when a type the module builds lists none of its properties' {
        $fixture = New-Fixture -Functions @($script:GetHealthUnlisted) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMHealth') } -NonApiOutputTypes @{ 'JIM.HealthSummary' = 'Built by the cmdlet from several API calls' }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'JIM\.HealthSummary'
    }

    It 'fails on an allowed output type that no cmdlet declares' {
        $fixture = New-Fixture -NonApiOutputTypes @{ 'JIM.Stale' = 'Left behind' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'JIM\.Stale'
    }

    It "tables a type the module names itself with the properties of the API schema it carries" {
        $fixture = New-Fixture -Functions @($script:GetGadget) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMGadget') } -OutputTypeSchemas @{ 'JIM.Gadget' = 'WidgetHeader' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $types = @(Get-OutputSections (Get-Block (Get-Page $fixture) 'Get-JIMGadget'))
        $types[0].Label | Should -Be ''
        @($types[0].Table | ForEach-Object Property) | Should -Be @('Id', 'Name', 'IsEnabled', 'Status', 'Count', 'Tags')
        ($types[0].Table | Where-Object Property -eq 'Id').Description | Should -Be "The Widget's identifier."
    }

    It 'tables the properties a cmdlet adds to the API object after the schema''s own' {
        $withParts = $script:GetGadget -replace '(?m)^    #>', "`n    .OUTPUTS`n        Each Gadget.`n`n        JIM.Gadget`n        Parts (string[]): The Gadget's parts, which the module adds.`n    #>"
        $fixture = New-Fixture -Functions @($withParts) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMGadget') } -OutputTypeSchemas @{ 'JIM.Gadget' = 'WidgetHeader' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $block = Get-Block (Get-Page $fixture) 'Get-JIMGadget'
        $types = @(Get-OutputSections $block)
        @($types[0].Table | ForEach-Object Property) | Should -Be @('Id', 'Name', 'IsEnabled', 'Status', 'Count', 'Tags', 'Parts')
        ($types[0].Table | Where-Object Property -eq 'Parts').Description | Should -Be "The Gadget's parts, which the module adds."
        $block | Should -Not -Match 'which the module adds\.\s*\n\s*JIM\.Gadget' -Because 'the list is tabled, not repeated as prose'
    }

    It 'fails on an added property the schema already has' {
        $clash = $script:GetGadget -replace '(?m)^    #>', "`n    .OUTPUTS`n        Each Gadget.`n`n        JIM.Gadget`n        Name (string): A second Name.`n    #>"
        $fixture = New-Fixture -Functions @($clash) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMGadget') } -OutputTypeSchemas @{ 'JIM.Gadget' = 'WidgetHeader' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Name.*JIM\.Gadget.*WidgetHeader'
    }

    It 'fails on a property list for a type the cmdlet does not declare' {
        $stray = $script:GetGadget -replace '(?m)^    #>', "`n    .OUTPUTS`n        Each Gadget.`n`n        JIM.Gizmo`n        Parts (string[]): The parts.`n    #>"
        $fixture = New-Fixture -Functions @($stray) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMGadget') } -OutputTypeSchemas @{ 'JIM.Gadget' = 'WidgetHeader' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'JIM\.Gizmo'
    }

    It 'takes a label from the .OUTPUTS help for types its parameter sets cannot tell apart, and keeps it out of the prose' {
        $fixture = New-Fixture -Functions @($script:TestWidget) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Test-JIMWidget') } -NonApiOutputTypes @{ 'JIM.HealthSummary' = 'Built by the cmdlet' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $block = Get-Block (Get-Page $fixture) 'Test-JIMWidget'
        $sections = @(Get-OutputSections $block)
        $sections.Label | Should -Be @('By default', 'With -Quiet')
        $sections[0].Table.Property | Should -Be @('Status')
        $sections[1].Rest | Should -Be ' a `bool`.'
        Get-Subsection $block 'Output' | Should -Not -Match 'System\.Boolean|JIM\.HealthSummary'
    }

    It 'fails when a parameter set returns two types and the help labels neither' {
        $unlabelled = $script:TestWidget -replace 'JIM\.HealthSummary: By default', 'JIM.HealthSummary' -replace '(?m)^\s*System\.Boolean: With -Quiet\s*$', ''
        $fixture = New-Fixture -Functions @($unlabelled) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Test-JIMWidget') } -NonApiOutputTypes @{ 'JIM.HealthSummary' = 'Built by the cmdlet' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Test-JIMWidget.*JIM\.HealthSummary.*System\.Boolean'
    }

    It 'fails on an output type declared for a parameter set the cmdlet does not have' {
        $typo = $script:GetWidget -replace "'JIM\.WidgetDetail', ParameterSetName = 'ById'", "'JIM.WidgetDetail', ParameterSetName = 'ByIdd'"
        $fixture = New-Fixture -Functions @($typo, $script:RemoveWidget)
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Get-JIMWidget.*ByIdd'
    }

    It 'fails on a type mapped to a schema that no cmdlet declares' {
        $fixture = New-Fixture -OutputTypeSchemas @{ 'JIM.Stale' = 'WidgetHeader' }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'JIM\.Stale'
    }

    It 'fails on a type mapped to a schema the OpenAPI document does not have' {
        $fixture = New-Fixture -Functions @($script:GetGadget) -Pages @{ 'widgets.md' = "# Widgets`n`n" + (New-Section 'Get-JIMGadget') } -OutputTypeSchemas @{ 'JIM.Gadget' = 'GadgetResponse' }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'GadgetResponse'
    }

    It 'fails on a BEGIN marker with no END' {
        $fixture = New-Fixture -Pages @{ 'widgets.md' = "# Widgets`n`n## Get-JIMWidget`n`n<!-- BEGIN GENERATED: Get-JIMWidget -->`n" }
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'END'
    }

    It 'names the page of a cmdlet section whose heading opens with an icon (STY-3)' {
        $page = "# Widgets`n`n" + (New-Section 'Get-JIMWidget') + "`n## :material-delete-outline: Remove-JIMWidget`n`nHand-written, not generated yet.`n"
        $fixture = New-Fixture -Pages @{ 'widgets.md' = $page }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'widgets\.md.*Remove-JIMWidget has no generated block;'
    }

    It 'fails on a cmdlet section with no block, naming the page and the cmdlet, and leaves the section alone' {
        $page = "# Widgets`n`n" + (New-Section 'Get-JIMWidget') + "`n## Remove-JIMWidget`n`nHand-written, not generated yet.`n"
        $fixture = New-Fixture -Pages @{ 'widgets.md' = $page }
        Invoke-Generator $fixture | Out-Null
        $result = Invoke-Generator $fixture -Check
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'widgets\.md.*Remove-JIMWidget has no generated block'
        Get-Page $fixture | Should -Match 'Hand-written, not generated yet\.'
    }

    It 'fails on every exported cmdlet with no block, whether or not it has a section, whatever its name' {
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
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'widgets\.md.*Connect-JIM has no generated block'
        $result.Output | Should -Match 'Remove-JIMWidget has no generated block, and no section on any page'
        $result.Output | Should -Not -Match 'Get-JIMWidget has no generated block'
    }
}
