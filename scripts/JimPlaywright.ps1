# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Shared helper: locates a playwright package for the browser-driven docs checks.

.DESCRIPTION
    Two checks measure the docs site in a real layout engine, because neither question can be
    answered from the source: Update-DiagramLabelCuts.ps1 needs rendered text metrics, and
    Test-DiagramLightbox.ps1 needs the overlay's computed stage geometry. Both need a browser,
    and neither should add a dependency to get one.

    Nothing extra is installed: .devcontainer/setup.sh already installs @playwright/mcp globally
    for the Playwright MCP server and downloads the matching Chromium, so this reuses that copy.
    Preference order is an explicit override, then the @playwright/mcp copy (whose bundled
    playwright-core revision is the one whose browser was actually downloaded), then any plain
    global playwright / playwright-core.

    Dot-source this file to get Resolve-PlaywrightModule:
        . (Join-Path $PSScriptRoot 'JimPlaywright.ps1')
#>

Set-StrictMode -Version Latest

function Resolve-PlaywrightModule {
    <#
    .SYNOPSIS
        Returns the path of a playwright package Node can require, or throws with how to get one.
    #>
    [CmdletBinding()]
    param()

    if ($env:JIM_PLAYWRIGHT_MODULE) {
        if (Test-Path -LiteralPath $env:JIM_PLAYWRIGHT_MODULE) { return $env:JIM_PLAYWRIGHT_MODULE }
        throw "JIM_PLAYWRIGHT_MODULE is set but does not exist: $($env:JIM_PLAYWRIGHT_MODULE)"
    }

    $globalRoot = $null
    try { $globalRoot = (& npm root -g 2>$null | Select-Object -First 1) } catch { $globalRoot = $null }

    if ($globalRoot) {
        $candidates = @(
            (Join-Path $globalRoot '@playwright/mcp/node_modules/playwright-core'),
            (Join-Path $globalRoot 'playwright'),
            (Join-Path $globalRoot 'playwright-core')
        )
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }

    throw @'
Could not find a playwright package to drive a browser with.

The dev container installs one already (.devcontainer/setup.sh, "Installing Playwright MCP
browser"); re-run that step, or point JIM_PLAYWRIGHT_MODULE at a playwright/playwright-core
install of your own.
'@
}
