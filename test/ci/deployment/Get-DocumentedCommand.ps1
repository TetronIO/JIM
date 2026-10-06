# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Returns a command as a documentation page gives it, for a check to run exactly as written.

.DESCRIPTION
    The documentation gives a command per runtime, in tabs (=== "Docker", === "Podman") under the heading of the
    step it belongs to, or under a numbered step of a section (1. **Restore the database** ...). This returns the
    first fenced block under the named tab in the named heading's section, or in the named step of it, without the
    indentation the tab gives it; with no tab named, the first one outside every tab, as a command for either
    runtime is given. A check that runs the page's own text, rather than a copy of it, fails when the page goes
    wrong, as the Docker key backup did when it relied on an image an air-gapped host does not have (#1949), the
    rootless Podman one did when it wrote an empty archive (#1954), the database restore did when it restored
    over the existing database (#1951), and the rootless systemctl command did on a host without
    systemd-container (#1955).

.PARAMETER Path
    The documentation page.

.PARAMETER Heading
    The heading of the section, without its leading #s or its {#anchor}. A heading that starts with an emoji
    matches without it.

.PARAMETER Step
    The bold title a numbered step of the section starts with, such as Restore the database, for a section that
    gives a tab of the same name for each of several steps.

.PARAMETER Tab
    The tab's title, such as Docker. Leave it out for a command outside every tab.

.EXAMPLE
    ./test/ci/deployment/Get-DocumentedCommand.ps1 -Path ./docs/administration/backup-recovery.md -Heading 'Restoring' -Tab Docker

.EXAMPLE
    ./test/ci/deployment/Get-DocumentedCommand.ps1 -Path ./docs/administration/backup-recovery.md -Heading 'Restoring' -Step 'Restore the database' -Tab Docker

.EXAMPLE
    ./test/ci/deployment/Get-DocumentedCommand.ps1 -Path ./docs/administration/podman.md -Heading 'Rootless commands'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$Heading,

    [string]$Step,

    [string]$Tab
)

$ErrorActionPreference = 'Stop'

$lines = @(Get-Content -Path $Path)
$start = -1
$level = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^(#{1,6})\s+(.*?)\s*(\{#[^}]*\})?\s*$') {
        $text = $Matches[2]
        if ($text -eq $Heading -or $text -like "* $Heading") {
            $start = $i
            $level = $Matches[1].Length
            break
        }
    }
}
if ($start -lt 0) {
    throw "$Path has no heading '$Heading'"
}

# Whether the line is in the named step, or in any part of the section when no step is named.
$inStep = -not $Step
# The open tab's title and the indentation of its === line. A tab holds the lines indented further than that,
# so a line that is not ends it, and what follows is outside every tab.
$tabTitle = $null
$tabIndent = -1
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^(#{1,6})\s' -and $Matches[1].Length -le $level) {
        break
    }
    if ($line -match '^(\s*)=== "(.*)"\s*$') {
        $tabIndent = $Matches[1].Length
        $tabTitle = $Matches[2]
        continue
    }
    if ($tabIndent -ge 0 -and $line.Trim() -and ($line.Length - $line.TrimStart().Length) -le $tabIndent) {
        $tabIndent = -1
        $tabTitle = $null
    }
    if ($line -match '^\s*\d+\.\s+\*\*(.+?)\*\*') {
        $inStep = -not $Step -or $Matches[1] -eq $Step
        continue
    }
    $wanted = $inStep -and $(if ($Tab) { $tabTitle -eq $Tab } else { $tabIndent -lt 0 })
    if ($wanted -and $line -match '^(\s*)```') {
        $indent = $Matches[1]
        $block = for ($j = $i + 1; $j -lt $lines.Count -and $lines[$j] -notmatch '^\s*```\s*$'; $j++) {
            if ($lines[$j].StartsWith($indent)) { $lines[$j].Substring($indent.Length) } else { $lines[$j].TrimStart() }
        }
        return (@($block) -join "`n")
    }
}
$where = if ($Tab) { "under a `"$Tab`" tab" } else { 'outside a tab' }
if ($Step) {
    throw "The section '$Heading' of $Path has no step '$Step' with a command $where"
}
throw "The section '$Heading' of $Path has no command $where"
