# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Lint-DocExamples.ps1.

.DESCRIPTION
    Exercises the parameter-alias-in-documentation rule: a doc example or a
    cmdlet's own .EXAMPLE help must invoke a JIM cmdlet using each parameter's
    real name, never a declared [Alias(...)] spelling. The motivating defect:
    Get-JIMRunProfile's -ConnectedSystemId carries [Alias('Id')] for pipeline
    convenience, so "Get-JIMRunProfile -Id 42" silently means "every Run
    Profile on Connected System 42", not "Run Profile 42".

    All fixtures are written under $TestDrive; none of this depends on the
    live repo's cmdlets or docs.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Lint-DocExamples.ps1')).Path

    # Writes a set of module (.ps1) and docs (.md) fixture files under fresh,
    # uniquely-named subdirectories of $TestDrive, so each test gets an
    # isolated ModulePath/DocsPath pair.
    function New-LintFixture {
        param(
            [hashtable]$ModuleFiles = @{},
            [hashtable]$DocFiles = @{}
        )

        $suffix = [guid]::NewGuid().ToString('N')
        $moduleRoot = Join-Path $TestDrive "Module_$suffix"
        $docsRoot = Join-Path $TestDrive "Docs_$suffix"
        New-Item -ItemType Directory -Path $moduleRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $docsRoot -Force | Out-Null

        foreach ($rel in $ModuleFiles.Keys) {
            $full = Join-Path $moduleRoot $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            Set-Content -Path $full -Value $ModuleFiles[$rel] -Encoding utf8
        }

        foreach ($rel in $DocFiles.Keys) {
            $full = Join-Path $docsRoot $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            Set-Content -Path $full -Value $DocFiles[$rel] -Encoding utf8
        }

        [pscustomobject]@{ ModuleRoot = $moduleRoot; DocsRoot = $docsRoot }
    }

    # Invokes the script as CI does (a child pwsh process) and returns the
    # exit code plus merged output, so assertions test the real entry-point
    # contract rather than an internal function.
    function Invoke-Lint {
        param([string]$ModuleRoot, [string]$DocsRoot)
        $output = pwsh -NoProfile -File $script:ScriptPath -ModulePath $ModuleRoot -DocsPath $DocsRoot 2>&1
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }

    # A cmdlet whose real parameter is ConnectedSystemId, aliased to Id for
    # pipeline convenience - the exact shape of the real Get-JIMRunProfile defect.
    $script:WidgetCmdlet = @'
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMWidget {
    <#
    .SYNOPSIS
        Gets Widgets from JIM.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System to get Widgets for.

    .PARAMETER Name
        Filter Widgets by name.

    .EXAMPLE
        Get-JIMWidget -ConnectedSystemId 1

        Gets all Widgets for Connected System ID 1.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [string]$Name
    )

    process {
        # fixture: no real body needed for lint purposes
    }
}
'@

    # A cmdlet whose real parameter genuinely IS named Id (no alias at all) -
    # using -Id here must never be flagged.
    $script:GadgetCmdlet = @'
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMGadget {
    <#
    .SYNOPSIS
        Gets Gadgets from JIM.

    .PARAMETER Id
        The unique identifier of the Gadget.

    .EXAMPLE
        Get-JIMGadget -Id 5

        Gets the Gadget with ID 5.
    #>
    [CmdletBinding()]
    param(
        [int]$Id
    )

    process {
        # fixture: no real body needed for lint purposes
    }
}
'@

    # A cmdlet with an unaliased -Identity parameter AND a separate, unrelated
    # aliased parameter (-TargetId, alias Id). Exercises that "-Identity"
    # never collides with the "-Id" alias of a different parameter (a naive
    # prefix/substring match would confuse the two).
    $script:ThingCmdlet = @'
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMThing {
    <#
    .SYNOPSIS
        Updates a Thing in JIM.

    .PARAMETER Identity
        The literal identity string of the Thing. Not aliased.

    .PARAMETER TargetId
        The numeric ID of the Thing to update.

    .EXAMPLE
        Set-JIMThing -Identity "abc" -TargetId 1

        Updates the Thing.
    #>
    [CmdletBinding()]
    param(
        [string]$Identity,

        [Alias('Id')]
        [int]$TargetId
    )

    process {
        # fixture: no real body needed for lint purposes
    }
}
'@

    # A cmdlet whose OWN comment-based help .EXAMPLE misuses its declared
    # alias - exercises the module-help scan, independent of any docs page.
    $script:BadExampleCmdlet = @'
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMWidgetBad {
    <#
    .SYNOPSIS
        Gets Widgets from JIM (fixture with a self-example that misuses its own alias).

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System to get Widgets for.

    .EXAMPLE
        Get-JIMWidgetBad -Id 7

        Gets all Widgets for Connected System ID 7.
    #>
    [CmdletBinding()]
    param(
        [Alias('Id')]
        [int]$ConnectedSystemId
    )

    process {
        # fixture: no real body needed for lint purposes
    }
}
'@
}

Describe 'Lint-DocExamples parameter alias detection' {

    It 'passes a clean fixture where docs use the real parameter name' {
        $fixture = New-LintFixture -ModuleFiles @{ 'Get-JIMWidget.ps1' = $script:WidgetCmdlet } -DocFiles @{
            'widgets.md' = @'
# Widgets

```powershell
Get-JIMWidget -ConnectedSystemId 1
```
'@
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 0
    }

    It 'fails when a doc example invokes a cmdlet via a parameter alias, and reports file, line, alias and real name' {
        $fixture = New-LintFixture -ModuleFiles @{ 'Get-JIMWidget.ps1' = $script:WidgetCmdlet } -DocFiles @{
            'widgets.md' = @'
# Widgets

```powershell
Get-JIMWidget -Id 42
```
'@
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'widgets\.md:4'
        $r.Output | Should -Match 'Get-JIMWidget invoked via alias -Id'
        $r.Output | Should -Match 'real parameter name -ConnectedSystemId'
    }

    It 'does not flag a parameter table row that documents an alias (false-positive guard)' {
        $fixture = New-LintFixture -ModuleFiles @{ 'Get-JIMWidget.ps1' = $script:WidgetCmdlet } -DocFiles @{
            'widgets.md' = @'
# Widgets

| Name | Type | Description |
|------|------|-------------|
| `ConnectedSystemId` | `int` | ID of the Connected System. Alias: `Id`. Accepts pipeline input by property name. |

Prose may also say the Alias: `Id` form is available, without triggering anything.

```powershell
Get-JIMWidget -ConnectedSystemId 1
```
'@
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 0
    }

    It 'does not flag -Id when the cmdlet''s real parameter genuinely is named Id' {
        $fixture = New-LintFixture -ModuleFiles @{ 'Get-JIMGadget.ps1' = $script:GadgetCmdlet } -DocFiles @{
            'gadgets.md' = @'
# Gadgets

```powershell
Get-JIMGadget -Id 5
```
'@
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 0
    }

    It 'does not flag -Identity as a collision with an unrelated parameter''s -Id alias' {
        $fixture = New-LintFixture -ModuleFiles @{ 'Set-JIMThing.ps1' = $script:ThingCmdlet } -DocFiles @{
            'things.md' = @'
# Things

```powershell
Set-JIMThing -Identity "abc" -TargetId 1
```
'@
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 0
    }

    It 'still flags -Id when it is genuinely used as the alias of the unrelated TargetId parameter' {
        $fixture = New-LintFixture -ModuleFiles @{ 'Set-JIMThing.ps1' = $script:ThingCmdlet } -DocFiles @{
            'things.md' = @'
# Things

```powershell
Set-JIMThing -Identity "abc" -Id 1
```
'@
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'Set-JIMThing invoked via alias -Id'
        $r.Output | Should -Match 'real parameter name -TargetId'
    }

    It 'flags a violation inside the module''s own .EXAMPLE comment-based help, independent of docs/' {
        $fixture = New-LintFixture -ModuleFiles @{ 'Get-JIMWidgetBad.ps1' = $script:BadExampleCmdlet } -DocFiles @{
            'widgets.md' = "# Widgets`n`nNo code examples here.`n"
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'Get-JIMWidgetBad\.ps1'
        $r.Output | Should -Match 'Get-JIMWidgetBad invoked via alias -Id'
        $r.Output | Should -Match 'real parameter name -ConnectedSystemId'
    }

    It 'attributes an alias used on a backtick continuation line to the correct source line' {
        $fixture = New-LintFixture -ModuleFiles @{ 'Get-JIMWidget.ps1' = $script:WidgetCmdlet } -DocFiles @{
            'widgets.md' = @'
# Widgets

```powershell
Get-JIMWidget `
    -Id 42
```
'@
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 1
        # Line 5 is where "-Id 42" actually sits, not line 4 (the cmdlet name).
        $r.Output | Should -Match 'widgets\.md:5'
    }

    It 'attributes a violation on the second command of a pipeline to that command, not the first' {
        $fixture = New-LintFixture -ModuleFiles @{
            'Get-JIMWidget.ps1'    = $script:WidgetCmdlet
            'Get-JIMWidgetBad.ps1' = $script:BadExampleCmdlet
        } -DocFiles @{
            'widgets.md' = @'
# Widgets

```powershell
Get-JIMWidget -ConnectedSystemId 1 | Get-JIMWidgetBad -Id 7
```
'@
        }

        $r = Invoke-Lint -ModuleRoot $fixture.ModuleRoot -DocsRoot $fixture.DocsRoot
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'Get-JIMWidgetBad invoked via alias -Id'
        $r.Output | Should -Not -Match 'Get-JIMWidget invoked via alias'
    }
}
