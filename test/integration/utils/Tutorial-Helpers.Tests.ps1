# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Tutorial-Helpers.ps1: reading a docs tutorial's PowerShell steps and checkpoints.

.DESCRIPTION
    The tested tutorial (getting-started/first-synchronisation.md, Scenario 027) keeps no copy of its
    own steps: the scenario reads them from the page, so the page and the test cannot drift apart
    (#1454, TUT-1). That makes the reader the thing that decides what is tested. These tests pin it
    down: only PowerShell tabs run, in page order; checkpoints sit between them where the page puts
    them; and a malformed marker fails loudly rather than being skipped, since a skipped checkpoint
    is an assertion silently dropped. The last Describe reads the real page, so a marker broken in a
    docs pull request fails there rather than at the next Pre-Release run.
#>

BeforeAll {
    . "$PSScriptRoot/Tutorial-Helpers.ps1"

    function New-TutorialPage {
        param([string]$Markdown)
        $path = Join-Path $TestDrive ("Tutorial_" + [guid]::NewGuid().ToString('N') + '.md')
        Set-Content -Path $path -Value $Markdown -Encoding utf8
        $path
    }

    $script:Page = @'
---
title: A Tutorial
---

# A Tutorial

## Before you begin

```powershell
Connect-JIM -Url https://jim.example.com
```

## Step 1: Create it

=== "Portal"

    1. Select **Create**.

    ```powershell
    Write-Output 'never run: a Portal tab'
    ```

=== "PowerShell"

    ```powershell
    $thing = New-Thing -Name 'A'
    Set-Thing -Id $thing.Id `
        -Value 1
    ```

## Step 2: Run it

=== "Docker"

    ```bash
    docker exec jim.worker true
    ```

=== "PowerShell"

    ```powershell
    $run = Start-Thing -Name 'A'
    ```

    ```powershell
    Wait-Thing $run
    ```

<!-- checkpoint: (Get-Thing -Count) -eq 25 -->
The list now holds 25 things.

<!-- checkpoint-file: /connector-files/tutorial/out.csv rows=25 -->
The file holds 25 rows.
'@
}

Describe 'Get-TutorialStep' {
    BeforeAll {
        $script:Steps = @(Get-TutorialStep -Path (New-TutorialPage $script:Page))
    }

    It 'returns the PowerShell tab blocks and the checkpoints, in page order' {
        $script:Steps.Kind | Should -Be @('Code', 'Code', 'Code', 'Checkpoint', 'FileCheckpoint')
    }

    It 'leaves out every other tab, and code outside any tab' {
        ($script:Steps | Where-Object Kind -eq 'Code').Code -join "`n" | Should -Not -Match 'never run|docker exec|Connect-JIM'
    }

    It 'removes the tab indentation and keeps a block''s lines together' {
        $expected = @'
$thing = New-Thing -Name 'A'
Set-Thing -Id $thing.Id `
    -Value 1
'@
        $script:Steps[0].Code | Should -Be ($expected -replace "`r`n", "`n")
    }

    It 'names the step each item belongs to, from the heading above it' {
        $script:Steps[0].Step | Should -Be 'Step 1: Create it'
        $script:Steps[3].Step | Should -Be 'Step 2: Run it'
    }

    It 'reads a checkpoint''s expression' {
        $script:Steps[3].Expression | Should -Be '(Get-Thing -Count) -eq 25'
    }

    It 'reads a file checkpoint''s path and row count' {
        $script:Steps[4].Path | Should -Be '/connector-files/tutorial/out.csv'
        $script:Steps[4].Rows | Should -Be 25
    }

    It 'records the line each item starts on, so a failure can name it' {
        $script:Steps[3].Line | Should -BeGreaterThan $script:Steps[2].Line
    }

    It 'fails on a checkpoint marker it cannot read, rather than skipping an assertion' {
        $path = New-TutorialPage "# T`n`n<!-- checkpoint-file: /connector-files/out.csv rows=many -->`n"
        { Get-TutorialStep -Path $path } | Should -Throw '*checkpoint*line 3*'
    }

    It 'skips a block whose language has a hyphen, such as a jim-diagram spec' {
        $markdown = @'
# T

```jim-diagram
archetype: flow
```

## Step 1: Do it

=== "PowerShell"

    ```powershell
    Get-Thing
    ```
'@
        $steps = @(Get-TutorialStep -Path (New-TutorialPage $markdown))
        $steps.Kind | Should -Be @('Code')
        $steps[0].Code | Should -Be 'Get-Thing'
    }

    It 'fails on an empty checkpoint' {
        $path = New-TutorialPage "# T`n`n<!-- checkpoint: -->`n"
        { Get-TutorialStep -Path $path } | Should -Throw '*checkpoint*'
    }
}

Describe 'The first synchronisation tutorial' {
    # The real page: what Scenario 027 executes at release time, checked statically on every pull request.
    BeforeAll {
        $script:TutorialPath = Join-Path $PSScriptRoot '../../../docs/getting-started/first-synchronisation.md'
        $script:Real = @(Get-TutorialStep -Path $script:TutorialPath)
    }

    It 'has PowerShell steps and checkpoints for the scenario to run' {
        @($script:Real | Where-Object Kind -eq 'Code').Count | Should -BeGreaterThan 0
        @($script:Real | Where-Object Kind -in 'Checkpoint', 'FileCheckpoint').Count | Should -BeGreaterThan 0
    }

    It 'gives PowerShell that parses, in every step and checkpoint' {
        $broken = foreach ($item in $script:Real) {
            $text = if ($item.Kind -eq 'Code') { $item.Code } elseif ($item.Kind -eq 'Checkpoint') { $item.Expression } else { continue }
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$errors) | Out-Null
            if ($errors) { "line $($item.Line): $($errors[0].Message)" }
        }
        $broken | Should -BeNullOrEmpty
    }

    It 'ends with a checkpoint, so the last thing the reader is told to expect is tested' {
        $script:Real[-1].Kind | Should -BeIn @('Checkpoint', 'FileCheckpoint')
    }
}
