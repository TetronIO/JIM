# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/screenshots/TerminalScreen.ps1.

.DESCRIPTION
    The terminal screenshots (#582) run real PowerShell commands in a 120x30 pseudo-terminal and redraw what the
    terminal showed. Everything that decides what appears on that screen is worked out here, before a browser draws
    it: which output belongs to which command, the colours the output carried, how the typed command is highlighted,
    where long lines wrap, and which 30 rows a terminal would be showing at the end.
#>

BeforeAll {
    # Capture-Screenshots.ps1 runs these under strict mode, so the tests do too.
    Set-StrictMode -Version Latest
    . (Join-Path $PSScriptRoot '..' 'screenshots' 'TerminalScreen.ps1')

    $script:Esc = [char]27
    $script:Bel = [char]7

    # The text a row shows, without its colours.
    function Get-RowText { param($Row) (@($Row.spans) | ForEach-Object { $_.text }) -join '' }
}

Describe 'Resolve-TerminalCommand' {
    It 'puts the portal address where the manifest writes {BaseUrl}' {
        Resolve-TerminalCommand -Command "Connect-JIM -Url '{BaseUrl}' -ApiKey `$apiKey" -BaseUrl 'http://localhost:5200' |
            Should -Be "Connect-JIM -Url 'http://localhost:5200' -ApiKey `$apiKey"
    }

    It 'leaves a command without the placeholder alone' {
        Resolve-TerminalCommand -Command 'Get-JIMSchedule' -BaseUrl 'http://localhost:5200' | Should -Be 'Get-JIMSchedule'
    }
}

Describe 'Get-TerminalCommandSpans' {
    It 'keeps every character of the command, in order' {
        $command = "Get-JIMSchedule | Select-Object name, isEnabled"
        (@(Get-TerminalCommandSpans -Command $command) | ForEach-Object { $_.text }) -join '' | Should -Be $command
    }

    It 'colours commands, parameters, strings and variables as PSReadLine does' {
        $spans = @(Get-TerminalCommandSpans -Command "Connect-JIM -Url 'http://localhost:5200' -ApiKey `$apiKey")
        $colourOf = @{}
        foreach ($span in $spans) { if ($span.text.Trim()) { $colourOf[$span.text] = $span.fg } }

        $colourOf['Connect-JIM'] | Should -Be 33
        $colourOf['-Url'] | Should -Be 90
        $colourOf["'http://localhost:5200'"] | Should -Be 36
        $colourOf['$apiKey'] | Should -Be 32
    }

    It 'colours a command after a pipe as a command, and leaves its arguments in the default colour' {
        $spans = @(Get-TerminalCommandSpans -Command 'Get-JIMSchedule | Select-Object name')
        ($spans | Where-Object text -eq 'Select-Object').fg | Should -Be 33
        ($spans | Where-Object text -eq 'name').fg | Should -BeNullOrEmpty
    }
}

Describe 'ConvertFrom-AnsiOutput' {
    It 'splits the output into rows' {
        $rows = @(ConvertFrom-AnsiOutput -Text "first`r`nsecond`nthird")
        $rows.Count | Should -Be 3
        Get-RowText $rows[1] | Should -Be 'second'
    }

    It 'carries a colour and bold until they are reset' {
        $rows = @(ConvertFrom-AnsiOutput -Text "$Esc[32;1mUrl$Esc[0m : http://localhost:5200")
        $spans = @($rows[0].spans)
        $spans[0].text | Should -Be 'Url'
        $spans[0].fg | Should -Be 32
        $spans[0].bold | Should -BeTrue
        $spans[1].text | Should -Be ' : http://localhost:5200'
        $spans[1].fg | Should -BeNullOrEmpty
        $spans[1].bold | Should -BeFalse
    }

    It 'ignores the cursor, mode and title sequences a terminal does not draw' {
        $rows = @(ConvertFrom-AnsiOutput -Text "$Esc[?25l$Esc]0;pwsh$Bel${Esc}=Ready$Esc[K$Esc[?25h")
        $rows.Count | Should -Be 1
        Get-RowText $rows[0] | Should -Be 'Ready'
    }

    It 'expands a tab to the next multiple of eight columns' {
        Get-RowText @(ConvertFrom-AnsiOutput -Text "ab`tc")[0] | Should -Be 'ab      c'
    }
}

Describe 'Split-TerminalTranscript' {
    It 'gives each command the output between its marker and the next' {
        $transcript = "Script started`r`n$Esc]9999;cmd:0${Bel}one`r`n$Esc]9999;cmd:1${Bel}two`r`n$Esc]9999;end${Bel}"
        $segments = @(Split-TerminalTranscript -Transcript $transcript -Count 2)
        $segments.Count | Should -Be 2
        $segments[0] | Should -Be "one`r`n"
        $segments[1] | Should -Be "two`r`n"
    }

    It 'says which command did not finish when the session stopped early' {
        $transcript = "$Esc]9999;cmd:0${Bel}one`r`n$Esc]9999;cmd:1${Bel}Connect-JIM: Authentication failed"
        { Split-TerminalTranscript -Transcript $transcript -Count 2 } |
            Should -Throw -ExpectedMessage '*command 2 of 2*Authentication failed*'
    }
}

Describe 'New-TerminalScreen' {
    BeforeAll {
        $script:Prompt = 'PS /Users/jim> '
    }

    It 'shows each command at a prompt, its output, a blank row, and ends at a prompt with the cursor' {
        $rows = @(New-TerminalScreen -Commands @('Get-JIMSchedule') -Outputs @("`r`nName`r`n----`r`nDaily`r`n`r`n`r`n") -Prompt $Prompt -Columns 120 -Rows 30)

        $rows | ForEach-Object { Get-RowText $_ } | Should -Be @('PS /Users/jim> Get-JIMSchedule', '', 'Name', '----', 'Daily', '', 'PS /Users/jim> ')
        $rows[-1].cursor | Should -BeTrue
        @($rows | Where-Object cursor).Count | Should -Be 1
    }

    It 'shows the prompt in the default colour and the command highlighted' {
        $rows = @(New-TerminalScreen -Commands @('Get-JIMSchedule') -Outputs @('') -Prompt $Prompt -Columns 120 -Rows 30)
        $spans = @($rows[0].spans)
        $spans[0].text | Should -Be $Prompt
        $spans[0].fg | Should -BeNullOrEmpty
        ($spans | Where-Object text -eq 'Get-JIMSchedule').fg | Should -Be 33
    }

    It 'wraps a line longer than the terminal onto the next row, keeping its colour' {
        $long = "$Esc[33m" + ('x' * 130) + "$Esc[0m"
        $rows = @(New-TerminalScreen -Commands @('Get-Thing') -Outputs @($long) -Prompt $Prompt -Columns 120 -Rows 30)

        Get-RowText $rows[1] | Should -Be ('x' * 120)
        Get-RowText $rows[2] | Should -Be ('x' * 10)
        @($rows[2].spans)[0].fg | Should -Be 33
    }

    It 'shows only the last rows when the session is longer than the terminal' {
        $output = (1..40 | ForEach-Object { "line $_" }) -join "`r`n"
        $rows = @(New-TerminalScreen -Commands @('Get-Thing') -Outputs @($output) -Prompt $Prompt -Columns 120 -Rows 30)

        $rows.Count | Should -Be 30
        Get-RowText $rows[0] | Should -Be 'line 13'
        Get-RowText $rows[-1] | Should -Be $Prompt
    }

    It 'needs one output for each command' {
        { New-TerminalScreen -Commands @('a', 'b') -Outputs @('') -Prompt $Prompt -Columns 120 -Rows 30 } |
            Should -Throw -ExpectedMessage '*2 command*1 output*'
    }
}
