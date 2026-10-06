# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Returns a command as a documentation page gives it, for a check to run exactly as written.

.DESCRIPTION
    The documentation gives a command per runtime, in tabs (=== "Docker", === "Podman") under the heading of the
    step it belongs to. This returns the first fenced block under the named tab in the named heading's section,
    without the indentation the tab gives it; with no tab named, the first one outside every tab, as a command
    for either runtime is given. A check that runs the page's own text, rather than a copy of it, fails when the
    page goes wrong, as the Docker key backup did when it relied on an image an air-gapped host does not have
    (#1949), and the rootless Podman one did when it wrote an empty archive (#1954).

.PARAMETER Path
    The documentation page.

.PARAMETER Heading
    The heading of the section, without its leading #s or its {#anchor}. A heading that starts with an emoji
    matches without it.

.PARAMETER Tab
    The tab's title, such as Docker. Leave it out for a command outside every tab.

.EXAMPLE
    ./test/ci/deployment/Get-DocumentedCommand.ps1 -Path ./docs/administration/backup-recovery.md -Heading 'Restoring' -Tab Docker

.EXAMPLE
    ./test/ci/deployment/Get-DocumentedCommand.ps1 -Path ./docs/administration/podman.md -Heading 'Rootless commands'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$Heading,

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
    $wanted = if ($Tab) { $tabTitle -eq $Tab } else { $tabIndent -lt 0 }
    if ($wanted -and $line -match '^(\s*)```') {
        $indent = $Matches[1]
        $block = for ($j = $i + 1; $j -lt $lines.Count -and $lines[$j] -notmatch '^\s*```\s*$'; $j++) {
            if ($lines[$j].StartsWith($indent)) { $lines[$j].Substring($indent.Length) } else { $lines[$j].TrimStart() }
        }
        return (@($block) -join "`n")
    }
}
if ($Tab) {
    throw "The section '$Heading' of $Path has no command under a `"$Tab`" tab"
}
throw "The section '$Heading' of $Path has no command outside a tab"
