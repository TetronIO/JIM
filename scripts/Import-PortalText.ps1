# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Adds or updates portal text in the resource files from a tab-separated list.

.DESCRIPTION
    Writes each row of a tab-separated file into src/JIM.Web/Resources/<Name>.resx (the English, en-GB) and its
    culture files (<Name>.en-US.resx, <Name>.fr.resx, <Name>.de.resx), creating any that do not exist. A key that is
    already present is updated in place; every other entry is left as it was. This is how portal text is added
    (engineering/LOCALISATION.md), so nobody edits resource XML by hand, where an unescaped & or < breaks the build.

    The file's first line is a header naming its columns, in any order:

        Key    en-GB    en-US    fr    de    Comment

    Key and en-GB are required. An empty en-US, fr or de cell writes nothing for that language: en-US holds only the
    strings whose spelling differs, and a language not yet being translated is left out. Lines starting with # are
    skipped. A literal \n in a cell is written as a line break, for a message that is lines (the cell itself cannot
    hold one).

    Each row is checked before anything is written: the key must be a C# identifier, and every translation must use
    exactly the English string's {n} placeholders.

.PARAMETER Path
    The tab-separated file to import.

.PARAMETER Name
    The resource file's name without extension, e.g. ActivityText. Ends in "Text" by convention.

.PARAMETER ResourceFolder
    Where the resource files live. Defaults to src/JIM.Web/Resources.

.EXAMPLE
    pwsh ./scripts/Import-PortalText.ps1 -Path /tmp/activity.tsv -Name ActivityText
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Z][A-Za-z0-9]*$')]
    [string]$Name,

    [string]$ResourceFolder = (Join-Path $PSScriptRoot '..' 'src' 'JIM.Web' 'Resources')
)

$ErrorActionPreference = 'Stop'

$header = @'
<?xml version="1.0" encoding="utf-8"?>
<!-- Copyright (c) Tetron Limited. All rights reserved. -->
<!-- Licensed under the Tetron Commercial License. See LICENSE file in the project root. -->
<root>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
</root>
'@

function Get-Placeholders([string]$text) {
    [regex]::Matches($text, '\{(\d+)(?:[,:][^}]*)?\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
}

function Open-Resx([string]$file) {
    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $false
    if (Test-Path $file) {
        $document.Load($file)
    }
    else {
        $document.LoadXml($header)
    }
    return $document
}

function Set-Entry([System.Xml.XmlDocument]$document, [string]$key, [string]$value, [string]$comment) {
    $data = $document.SelectSingleNode("/root/data[@name='$key']")
    if ($null -eq $data) {
        $data = $document.CreateElement('data')
        $data.SetAttribute('name', $key)
        $space = $document.CreateAttribute('xml', 'space', 'http://www.w3.org/XML/1998/namespace')
        $space.Value = 'preserve'
        $data.Attributes.Append($space) | Out-Null
        $document.DocumentElement.AppendChild($data) | Out-Null
    }

    $valueNode = $data.SelectSingleNode('value')
    if ($null -eq $valueNode) {
        $valueNode = $document.CreateElement('value')
        $data.AppendChild($valueNode) | Out-Null
    }
    $valueNode.InnerText = $value

    $commentNode = $data.SelectSingleNode('comment')
    if ($comment) {
        if ($null -eq $commentNode) {
            $commentNode = $document.CreateElement('comment')
            $data.AppendChild($commentNode) | Out-Null
        }
        $commentNode.InnerText = $comment
    }
}

function Save-Resx([System.Xml.XmlDocument]$document, [string]$file) {
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.NewLineChars = "`n"
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.Xml.XmlWriter]::Create($file, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
    Add-Content -Path $file -Value '' -NoNewline:$false
}

if (-not (Test-Path $Path)) { throw "No file at '$Path'." }

$lines = Get-Content -Path $Path -Encoding utf8 | Where-Object { $_ -and -not $_.StartsWith('#') }
if ($lines.Count -lt 2) { throw "'$Path' has a header and no rows." }

$columns = $lines[0].Split("`t") | ForEach-Object { $_.Trim() }
foreach ($required in 'Key', 'en-GB') {
    if ($columns -notcontains $required) { throw "The header must name a '$required' column; it has: $($columns -join ', ')." }
}

$rows = @()
$problems = [System.Collections.Generic.List[string]]::new()
$seen = @{}
for ($i = 1; $i -lt $lines.Count; $i++) {
    $cells = $lines[$i].Split("`t")
    $row = @{}
    for ($c = 0; $c -lt $columns.Count; $c++) {
        $row[$columns[$c]] = if ($c -lt $cells.Count) { $cells[$c].Replace('\n', "`n") } else { '' }
    }

    $key = $row['Key'].Trim()
    if ($key -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { $problems.Add("Line $($i + 1): '$key' is not a valid key (letters, digits and underscores)."); continue }
    if ($seen.ContainsKey($key)) { $problems.Add("Line $($i + 1): '$key' appears twice."); continue }
    $seen[$key] = $true
    if (-not $row['en-GB']) { $problems.Add("Line $($i + 1): '$key' has no en-GB text."); continue }

    $expected = (Get-Placeholders $row['en-GB']) -join ','
    foreach ($language in 'en-US', 'fr', 'de') {
        if ($row[$language]) {
            $actual = (Get-Placeholders $row[$language]) -join ','
            if ($actual -ne $expected) { $problems.Add("Line $($i + 1): '$key' $language uses {$actual} where en-GB uses {$expected}.") }
        }
    }

    $rows += [pscustomobject]@{ Key = $key; Row = $row }
}

if ($problems.Count -gt 0) {
    throw "Nothing was written:`n  " + ($problems -join "`n  ")
}

$targets = [ordered]@{ '' = 'en-GB'; '.en-US' = 'en-US'; '.fr' = 'fr'; '.de' = 'de' }
foreach ($suffix in $targets.Keys) {
    $language = $targets[$suffix]
    $file = Join-Path $ResourceFolder "$Name$suffix.resx"
    $withText = @($rows | Where-Object { $_.Row[$language] })
    if ($withText.Count -eq 0) { continue }

    $document = Open-Resx $file
    foreach ($entry in $withText) {
        $comment = if ($language -eq 'en-GB') { $entry.Row['Comment'] } else { $null }
        Set-Entry $document $entry.Key $entry.Row[$language] $comment
    }
    Save-Resx $document $file
    Write-Host "$Name$suffix.resx: $($withText.Count) entr$(if ($withText.Count -eq 1) { 'y' } else { 'ies' }) written."
}
