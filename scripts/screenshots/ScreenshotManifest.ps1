# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The screenshot manifest: reading and validating it, naming the files it produces, and checking image sizes.

.DESCRIPTION
    scripts/screenshots/manifest.json lists every published screenshot that Capture-Screenshots.ps1 produces (#582).
    A mistake in it stops the run here, before a browser starts, naming the entry at fault; and an image that comes out
    at any size other than its profile's is reported rather than published.

    Dot-source this file:
        . (Join-Path $PSScriptRoot 'screenshots' 'ScreenshotManifest.ps1')
#>

Set-StrictMode -Version Latest

$script:ScreenshotKinds = @('portal', 'terminal')
$script:ScreenshotThemes = @('dark', 'light')
$script:ScreenshotStepActions = @('goto', 'click', 'fill', 'waitFor', 'scroll', 'wait')
$script:ScreenshotLocatorKeys = @('text', 'role', 'name', 'selector', 'exact')

function Read-ScreenshotManifest {
    <#
    .SYNOPSIS
        Reads and validates a screenshot manifest file.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    ConvertFrom-ScreenshotManifest -Json (Get-Content -LiteralPath $Path -Raw)
}

function ConvertFrom-ScreenshotManifest {
    <#
    .SYNOPSIS
        Validates a screenshot manifest and returns its profiles (keyed by name, with the image size each produces) and
        its screenshots. Throws on the first problem, naming the profile or screenshot at fault.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Json)

    $raw = $Json | ConvertFrom-Json -AsHashtable

    $profiles = @{}
    $rawProfiles = $raw['profiles']
    if ($rawProfiles -isnot [System.Collections.IDictionary] -or $rawProfiles.Count -eq 0) {
        throw 'The screenshot manifest defines no profiles.'
    }
    foreach ($profileName in $rawProfiles.Keys) {
        $profiles[$profileName] = ConvertTo-ScreenshotProfile -Name $profileName -Definition $rawProfiles[$profileName]
    }

    $rawScreenshots = @($raw['screenshots'] | Where-Object { $null -ne $_ })
    if ($rawScreenshots.Count -eq 0) {
        throw 'The screenshot manifest lists no screenshots.'
    }

    $names = [System.Collections.Generic.HashSet[string]]::new()
    $screenshots = foreach ($entry in $rawScreenshots) {
        $screenshot = Assert-ScreenshotEntry -Entry $entry -Profiles $profiles
        if (-not $names.Add($screenshot.name)) {
            throw "Screenshot '$($screenshot.name)' is listed more than once."
        }
        $screenshot
    }

    [pscustomobject]@{ Profiles = $profiles; Screenshots = @($screenshots) }
}

function ConvertTo-ScreenshotProfile {
    param([string]$Name, $Definition)

    $viewport = if ($Definition -is [System.Collections.IDictionary]) { $Definition['viewport'] } else { $null }
    $width = if ($viewport -is [System.Collections.IDictionary]) { $viewport['width'] } else { $null }
    $height = if ($viewport -is [System.Collections.IDictionary]) { $viewport['height'] } else { $null }
    $density = if ($Definition -is [System.Collections.IDictionary]) { $Definition['deviceScaleFactor'] } else { $null }

    if (-not (Test-PositiveNumber $width -Integer) -or -not (Test-PositiveNumber $height -Integer) -or -not (Test-PositiveNumber $density)) {
        throw "The screenshot profile '$Name' needs a viewport with a positive whole width and height, and a positive deviceScaleFactor."
    }

    [pscustomobject]@{
        Name              = $Name
        ViewportWidth     = [int]$width
        ViewportHeight    = [int]$height
        DeviceScaleFactor = [double]$density
        ImageWidth        = [int][Math]::Round($width * $density)
        ImageHeight       = [int][Math]::Round($height * $density)
        Transparent       = [bool]($Definition['transparent'])
    }
}

function Assert-ScreenshotEntry {
    param($Entry, [hashtable]$Profiles)

    $name = if ($Entry -is [System.Collections.IDictionary]) { $Entry['name'] } else { $null }
    if ($name -isnot [string] -or $name -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') {
        throw "A screenshot has no valid name ('$name'): use lower-case letters, digits and single hyphens, as the published file names do."
    }

    $kind = $Entry['kind']
    if ($kind -notin $script:ScreenshotKinds) {
        throw "Screenshot '$name' has kind '$kind'; expected one of: $($script:ScreenshotKinds -join ', ')."
    }

    $profileName = $Entry['profile']
    if (-not $Profiles.ContainsKey("$profileName")) {
        throw "Screenshot '$name' uses profile '$profileName', which the manifest does not define."
    }

    $themes = @($Entry['themes'] | Where-Object { $null -ne $_ })
    if ($themes.Count -eq 0) {
        throw "Screenshot '$name' names no theme; give it 'dark', 'light' or both."
    }
    foreach ($theme in $themes) {
        if ($theme -notin $script:ScreenshotThemes) {
            throw "Screenshot '$name' names theme '$theme'; expected 'dark' or 'light'."
        }
    }
    if (@($themes | Select-Object -Unique).Count -ne $themes.Count) {
        throw "Screenshot '$name' names the same theme twice."
    }

    if ($kind -eq 'portal') {
        $steps = @($Entry['steps'] | Where-Object { $null -ne $_ })
        if ($steps.Count -eq 0) {
            throw "Screenshot '$name' has no steps; a portal screenshot needs at least a goto."
        }
        for ($i = 0; $i -lt $steps.Count; $i++) {
            Assert-ScreenshotStep -ScreenshotName $name -Number ($i + 1) -Step $steps[$i]
        }
    }
    else {
        $commands = @($Entry['commands'] | Where-Object { $_ -is [string] -and $_.Trim() -ne '' })
        if (@($commands | Where-Object { -not $_.StartsWith('!') }).Count -eq 0) {
            throw "Screenshot '$name' has no command to show; lines starting with '!' are set-up and do not appear."
        }
    }

    [pscustomobject]$Entry
}

function Assert-ScreenshotStep {
    param([string]$ScreenshotName, [int]$Number, $Step)

    $where = "Screenshot '$ScreenshotName', step $Number"
    if ($Step -isnot [System.Collections.IDictionary] -or $Step.Count -ne 1) {
        throw "${where}: a step is one action: $($script:ScreenshotStepActions -join ', ')."
    }
    $action = @($Step.Keys)[0]
    $value = $Step[$action]

    switch ($action) {
        'goto' {
            if ($value -isnot [string] -or -not $value.StartsWith('/')) {
                throw "${where}: goto takes a path on the portal, starting with '/'."
            }
        }
        'wait' {
            if (-not (Test-PositiveNumber $value -Integer) -or $value -gt 30000) {
                throw "${where}: wait takes a number of milliseconds between 1 and 30000."
            }
        }
        { $_ -in 'click', 'waitFor', 'scroll' } {
            Assert-ScreenshotLocator -Where $where -Action $action -Locator $value
        }
        'fill' {
            # A locator for the field, plus the text to type into it (a search box, say).
            if ($value -isnot [System.Collections.IDictionary] -or $value['value'] -isnot [string] -or $value['value'] -eq '') {
                throw "${where}: fill takes a locator for the field and the text to type as value."
            }
            $field = [ordered]@{}
            foreach ($key in @($value.Keys | Where-Object { $_ -ne 'value' })) { $field[$key] = $value[$key] }
            Assert-ScreenshotLocator -Where $where -Action $action -Locator $field
        }
        default {
            throw "${where}: '$action' is not an action; expected one of: $($script:ScreenshotStepActions -join ', ')."
        }
    }
}

function Assert-ScreenshotLocator {
    param([string]$Where, [string]$Action, $Locator)

    if ($Locator -isnot [System.Collections.IDictionary]) {
        throw "${Where}: $Action takes a locator: { text }, { role, name } or { selector }."
    }
    $unknown = @($Locator.Keys | Where-Object { $_ -notin $script:ScreenshotLocatorKeys })
    if ($unknown.Count -gt 0) {
        throw "${Where}: the locator has unknown keys: $($unknown -join ', ')."
    }
    $ways = @('text', 'role', 'selector' | Where-Object { $Locator.Contains($_) })
    if ($ways.Count -ne 1) {
        throw "${Where}: a locator finds its element one way: text, role (with name) or selector."
    }
    if ($Locator.Contains('role') -and -not ($Locator['name'] -is [string] -and $Locator['name'] -ne '')) {
        throw "${Where}: a role locator needs the element's accessible name."
    }
    if ($Locator.Contains('name') -and -not $Locator.Contains('role')) {
        throw "${Where}: name only applies to a role locator."
    }
}

function Test-PositiveNumber {
    param($Value, [switch]$Integer)

    if ($Value -isnot [int] -and $Value -isnot [long] -and $Value -isnot [double] -and $Value -isnot [decimal]) { return $false }
    if ($Integer -and $Value -ne [Math]::Floor($Value)) { return $false }
    return $Value -gt 0
}

function Get-ScreenshotFileNames {
    <#
    .SYNOPSIS
        The files a screenshot produces, one per theme: '<name>-<theme>.png' when it is published in both themes, and
        '<name>.png' when it is published in one, matching the names on junctional.io.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Screenshot)

    $themes = @($Screenshot.themes)
    foreach ($theme in $themes) {
        $fileName = if ($themes.Count -eq 1) { "$($Screenshot.name).png" } else { "$($Screenshot.name)-$theme.png" }
        [pscustomobject]@{ Theme = $theme; FileName = $fileName }
    }
}

function Get-PngDimensions {
    <#
    .SYNOPSIS
        Reads a PNG's width and height from its header.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $header = [byte[]]::new(24)
        $read = $stream.Read($header, 0, 24)
    }
    finally { $stream.Dispose() }

    $signature = 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
    $isPng = $read -eq 24 -and
        [System.BitConverter]::ToString($header, 0, 8) -eq [System.BitConverter]::ToString([byte[]]$signature) -and
        [System.Text.Encoding]::ASCII.GetString($header, 12, 4) -eq 'IHDR'
    if (-not $isPng) {
        throw "$Path is not a PNG."
    }

    # IHDR holds the width then the height, each a big-endian 32-bit integer.
    # Each byte is widened before shifting: -shl on a [byte] stays a byte and wraps to zero.
    $readInt32 = { param([int]$Offset) ([long]$header[$Offset] -shl 24) -bor ([long]$header[$Offset + 1] -shl 16) -bor ([long]$header[$Offset + 2] -shl 8) -bor [long]$header[$Offset + 3] }
    [pscustomobject]@{ Width = [int](& $readInt32 16); Height = [int](& $readInt32 20) }
}

function Find-ScreenshotSizeMismatch {
    <#
    .SYNOPSIS
        Returns a description of how an image's size differs from its profile's, or nothing when it matches.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ProfileName,
        [Parameter(Mandatory)][hashtable]$Profiles
    )

    $expected = $Profiles[$ProfileName]
    $actual = Get-PngDimensions -Path $Path
    if ($actual.Width -ne $expected.ImageWidth -or $actual.Height -ne $expected.ImageHeight) {
        "$(Split-Path -Leaf $Path) is $($actual.Width)x$($actual.Height); expected $($expected.ImageWidth)x$($expected.ImageHeight) for profile '$ProfileName'."
    }
}
