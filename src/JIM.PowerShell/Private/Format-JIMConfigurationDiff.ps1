# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMDiffColour {
    # Returns ANSI colour codes via $PSStyle (PowerShell 7.2+), or empty strings when $PSStyle is
    # unavailable or colour is disabled, so the rendered diff stays readable when captured or redirected.
    if ($PSStyle) {
        return [pscustomobject]@{
            Green = $PSStyle.Foreground.Green
            Red   = $PSStyle.Foreground.Red
            Amber = $PSStyle.Foreground.Yellow
            Dim   = $PSStyle.Foreground.BrightBlack
            Reset = $PSStyle.Reset
        }
    }

    return [pscustomobject]@{ Green = ''; Red = ''; Amber = ''; Dim = ''; Reset = '' }
}

function Format-JIMConfigurationDiffNode {
    # Recursively renders one node of a configuration diff tree as git-style coloured lines.
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [PSObject]$Node,

        [int]$Depth = 0,

        [Parameter(Mandatory)]
        [PSObject]$Style
    )

    $pad = '  ' * $Depth
    $changeType = [string]$Node.changeType
    $label = if ($Node.label) { $Node.label } else { $Node.key }

    if ($Node.nodeType -eq 'Scalar') {
        switch ($changeType) {
            'Added'   { "$($Style.Green)$pad+ $($label): $($Node.newValue)$($Style.Reset)" }
            'Removed' { "$($Style.Red)$pad- $($label): $($Node.oldValue)$($Style.Reset)" }
            'Modified' {
                if ($Node.isSecret) {
                    # Never render a secret's value; the keyed hash only tells us that it changed.
                    "$($Style.Amber)$pad~ $($label)  (secret changed; value hidden)$($Style.Reset)"
                }
                else {
                    "$($Style.Red)$pad- $($label): $($Node.oldValue)$($Style.Reset)"
                    "$($Style.Green)$pad+ $($label): $($Node.newValue)$($Style.Reset)"
                }
            }
            # 'Unchanged' produces no output, like a git diff.
        }
        return
    }

    # Object / Collection node.
    if ($changeType -eq 'Unchanged') {
        return
    }

    if ($changeType -eq 'Added' -or $changeType -eq 'Removed') {
        if ($changeType -eq 'Added') {
            "$($Style.Green)$pad+ $label$($Style.Reset)"
        }
        else {
            "$($Style.Red)$pad- $label$($Style.Reset)"
        }
        foreach ($child in $Node.children) {
            Format-JIMConfigurationDiffNode -Node $child -Depth ($Depth + 1) -Style $Style
        }
        return
    }

    # Modified container: a dim context header, then only the changed descendants below it.
    "$($Style.Dim)$pad$label$($Style.Reset)"
    foreach ($child in $Node.children) {
        Format-JIMConfigurationDiffNode -Node $child -Depth ($Depth + 1) -Style $Style
    }
}

function Format-JIMConfigurationDiff {
    # Renders a configuration diff (as returned by the change-history detail/compare endpoints) as a
    # git-style coloured unified diff. Emits strings so the output renders coloured in the terminal but
    # remains plain text when captured or redirected.
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [PSObject]$Diff
    )

    $style = Get-JIMDiffColour

    $from = if ($null -ne $Diff.oldVersion) { "v$($Diff.oldVersion)" } else { '(new)' }
    $to = if ($null -ne $Diff.newVersion) { "v$($Diff.newVersion)" } else { '(current)' }
    "$($style.Dim)$($Diff.objectType) `"$($Diff.objectName)`"  ($from -> $to)$($style.Reset)"

    $total = [int]$Diff.addedCount + [int]$Diff.removedCount + [int]$Diff.modifiedCount
    if ($total -eq 0) {
        "$($style.Dim)(no changes)$($style.Reset)"
        return
    }

    foreach ($child in $Diff.root.children) {
        Format-JIMConfigurationDiffNode -Node $child -Depth 0 -Style $style
    }
}
