# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Works out what a terminal screenshot shows (#582).

.DESCRIPTION
    A terminal screenshot runs real PowerShell commands in a pseudo-terminal (see terminal-session.ps1) and is then
    redrawn in a browser. Everything that decides what is on that screen is worked out here, so it can be tested:

    - which part of the recorded transcript belongs to which command;
    - the colours and bold the output carried, from its ANSI escape sequences;
    - how the typed command is highlighted, from PowerShell's own tokeniser with PSReadLine's default colours;
    - where a line longer than the terminal wraps;
    - which rows the terminal is showing at the end, which is its last rows when the session is longer.

    A screen is a list of rows. Each row carries spans of text, each with an ANSI foreground colour number (or none
    for the default) and whether it is bold; the last row also carries the cursor. The browser side draws the rows
    and nothing else.
#>

function New-TerminalSpan {
    param([string]$Text, $Fg, [bool]$Bold)
    [pscustomobject]@{ text = $Text; fg = $Fg; bold = $Bold }
}

function New-TerminalRow {
    param([object[]]$Spans, [bool]$Cursor)
    [pscustomobject]@{ spans = @($Spans); cursor = $Cursor }
}

function Get-TerminalRowLength {
    param($Row)
    $length = 0
    foreach ($span in @($Row.spans)) { $length += $span.text.Length }
    $length
}

# Puts the portal's address where a manifest command writes {BaseUrl}, so the screenshot shows the real address.
function Resolve-TerminalCommand {
    param([Parameter(Mandatory)][string]$Command, [Parameter(Mandatory)][string]$BaseUrl)
    $Command.Replace('{BaseUrl}', $BaseUrl.TrimEnd('/'))
}

# The colour PSReadLine gives a token by default, as an ANSI colour number; $null is the default foreground.
function Get-TerminalTokenColour {
    param([Parameter(Mandatory)][System.Management.Automation.Language.Token]$Token)

    if ($Token.TokenFlags.HasFlag([System.Management.Automation.Language.TokenFlags]::CommandName)) { return 33 }
    switch ($Token.Kind.ToString()) {
        'Parameter' { return 90 }
        { $_ -in 'StringLiteral', 'StringExpandable', 'HereStringLiteral', 'HereStringExpandable' } { return 36 }
        { $_ -in 'Variable', 'SplattedVariable' } { return 32 }
        'Number' { return 97 }
        'Comment' { return 32 }
    }
    $null
}

# The typed command as highlighted spans, every character kept in order.
function Get-TerminalCommandSpans {
    param([Parameter(Mandatory)][string]$Command)

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($Command, [ref]$tokens, [ref]$errors)

    $position = 0
    foreach ($token in @($tokens | Where-Object Kind -ne 'EndOfInput')) {
        $start = $token.Extent.StartOffset
        $end = $token.Extent.EndOffset
        if ($start -gt $position) { New-TerminalSpan -Text $Command.Substring($position, $start - $position) -Fg $null -Bold $false }
        if ($end -gt $start) { New-TerminalSpan -Text $Command.Substring($start, $end - $start) -Fg (Get-TerminalTokenColour -Token $token) -Bold $false }
        $position = [Math]::Max($position, $end)
    }
    if ($position -lt $Command.Length) { New-TerminalSpan -Text $Command.Substring($position) -Fg $null -Bold $false }
}

# Applies one SGR sequence's parameters to the current style.
function Update-TerminalStyle {
    param([Parameter(Mandatory)][hashtable]$Style, [string]$Parameters)

    $codes = @(if ([string]::IsNullOrEmpty($Parameters)) { 0 } else { $Parameters.Split(';') | ForEach-Object { if ($_ -eq '') { 0 } else { [int]$_ } } })
    for ($i = 0; $i -lt $codes.Count; $i++) {
        $code = $codes[$i]
        if ($code -eq 0) { $Style.Fg = $null; $Style.Bold = $false }
        elseif ($code -eq 1) { $Style.Bold = $true }
        elseif ($code -eq 22) { $Style.Bold = $false }
        elseif ($code -eq 39) { $Style.Fg = $null }
        elseif (($code -ge 30 -and $code -le 37) -or ($code -ge 90 -and $code -le 97)) { $Style.Fg = $code }
        elseif ($code -in 38, 48) {
            # Extended colours: 5;n picks from the 256-colour table, 2;r;g;b is a true colour. Only the first 16
            # entries of the table have a place in this terminal's palette, and only as a foreground.
            if ($i + 1 -lt $codes.Count -and $codes[$i + 1] -eq 5 -and $i + 2 -lt $codes.Count) {
                $index = $codes[$i + 2]
                if ($code -eq 38 -and $index -lt 8) { $Style.Fg = 30 + $index }
                elseif ($code -eq 38 -and $index -lt 16) { $Style.Fg = 90 + $index - 8 }
                $i += 2
            }
            elseif ($i + 1 -lt $codes.Count -and $codes[$i + 1] -eq 2) { $i += 4 }
        }
    }
}

# Rows of styled spans from text carrying ANSI escape sequences. Only colour and bold are drawn; cursor movement,
# mode switches and window titles are dropped, as pwsh writes none that change what the screen shows.
function ConvertFrom-AnsiOutput {
    param([AllowEmptyString()][string]$Text)

    $pattern = '\x1b\[([0-?]*)([ -/]*)([@-~])|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[\x30-\x5f]|\x1b|\r\n|\n|\r|\t|[^\x1b\r\n\t]+'
    $style = @{ Fg = $null; Bold = $false }
    $rows = [System.Collections.Generic.List[object]]::new()
    $spans = [System.Collections.Generic.List[object]]::new()
    $column = 0

    $append = {
        param([string]$Fragment)
        $last = if ($spans.Count -gt 0) { $spans[$spans.Count - 1] } else { $null }
        if ($null -ne $last -and $last.fg -eq $style.Fg -and $last.bold -eq $style.Bold) { $last.text += $Fragment }
        else { $spans.Add((New-TerminalSpan -Text $Fragment -Fg $style.Fg -Bold $style.Bold)) }
    }

    foreach ($match in [regex]::Matches($Text, $pattern)) {
        $value = $match.Value
        if ($match.Groups[3].Success) {
            # A CSI sequence. Only SGR (final byte m, no private prefix) changes what is drawn.
            if ($match.Groups[3].Value -eq 'm' -and $match.Groups[2].Value -eq '' -and $match.Groups[1].Value -notmatch '[<=>?]') {
                Update-TerminalStyle -Style $style -Parameters $match.Groups[1].Value
            }
        }
        elseif ($value -eq "`r`n" -or $value -eq "`n") {
            $rows.Add((New-TerminalRow -Spans $spans.ToArray() -Cursor $false))
            $spans.Clear()
            $column = 0
        }
        elseif ($value -eq "`t") {
            $width = 8 - ($column % 8)
            & $append (' ' * $width)
            $column += $width
        }
        elseif ($value[0] -eq [char]27 -or $value -eq "`r") { continue }
        else {
            & $append $value
            $column += $value.Length
        }
    }
    $rows.Add((New-TerminalRow -Spans $spans.ToArray() -Cursor $false))
    $rows.ToArray()
}

# The output of each command, from a transcript in which terminal-session.ps1 wrote a marker before each command
# and one after the last.
function Split-TerminalTranscript {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Transcript, [Parameter(Mandatory)][int]$Count)

    $markers = @([regex]::Matches($Transcript, '\x1b\]9999;(cmd:(\d+)|end)\x07'))
    for ($index = 0; $index -lt $Count; $index++) {
        $start = $markers | Where-Object { $_.Groups[2].Success -and [int]$_.Groups[2].Value -eq $index } | Select-Object -First 1
        $next = if ($null -ne $start) { $markers | Where-Object Index -gt $start.Index | Select-Object -First 1 } else { $null }
        if ($null -eq $start -or $null -eq $next) {
            $from = if ($null -ne $start) { $start.Index + $start.Length } elseif ($markers.Count -gt 0) { $markers[-1].Index } else { 0 }
            $tail = [regex]::Replace($Transcript.Substring($from), '\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b.', '').Trim()
            if ($tail.Length -gt 400) { $tail = '...' + $tail.Substring($tail.Length - 400) }
            throw "The terminal session stopped during command $($index + 1) of ${Count}: $tail"
        }
        $Transcript.Substring($start.Index + $start.Length, $next.Index - $start.Index - $start.Length)
    }
}

# Breaks a row longer than the terminal into rows of at most Columns characters, keeping each span's style.
function Split-TerminalRow {
    param([Parameter(Mandatory)]$Row, [Parameter(Mandatory)][int]$Columns)

    $rows = [System.Collections.Generic.List[object]]::new()
    $current = [System.Collections.Generic.List[object]]::new()
    $used = 0
    foreach ($span in @($Row.spans)) {
        $text = $span.text
        while ($text.Length -gt 0) {
            if ($used -eq $Columns) {
                $rows.Add((New-TerminalRow -Spans $current.ToArray() -Cursor $false))
                $current.Clear()
                $used = 0
            }
            $take = [Math]::Min($Columns - $used, $text.Length)
            $current.Add((New-TerminalSpan -Text $text.Substring(0, $take) -Fg $span.fg -Bold $span.bold))
            $used += $take
            $text = $text.Substring($take)
        }
    }
    $rows.Add((New-TerminalRow -Spans $current.ToArray() -Cursor $Row.cursor))
    $rows.ToArray()
}

# The rows a terminal shows after typing each command at the prompt and printing its output: a blank row after
# each command's output, the prompt again with the cursor at the end, and only the last Rows rows when the session
# is longer than the terminal.
function New-TerminalScreen {
    param(
        [Parameter(Mandatory)][string[]]$Commands,
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Outputs,
        [Parameter(Mandatory)][string]$Prompt,
        [int]$Columns = 120,
        [int]$Rows = 30
    )

    if ($Commands.Count -ne $Outputs.Count) {
        throw "New-TerminalScreen needs one output for each command; got $($Commands.Count) command(s) and $($Outputs.Count) output(s)."
    }

    $lines = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $Commands.Count; $i++) {
        $promptSpan = New-TerminalSpan -Text $Prompt -Fg $null -Bold $false
        $lines.Add((New-TerminalRow -Spans (@($promptSpan) + @(Get-TerminalCommandSpans -Command $Commands[$i])) -Cursor $false))

        $output = [System.Collections.Generic.List[object]]::new()
        foreach ($row in @(ConvertFrom-AnsiOutput -Text $Outputs[$i])) { $output.Add($row) }
        while ($output.Count -gt 0 -and (Get-TerminalRowLength -Row $output[$output.Count - 1]) -eq 0) { $output.RemoveAt($output.Count - 1) }
        foreach ($row in $output) { $lines.Add($row) }
        $lines.Add((New-TerminalRow -Spans @() -Cursor $false))
    }
    $lines.Add((New-TerminalRow -Spans @(New-TerminalSpan -Text $Prompt -Fg $null -Bold $false) -Cursor $true))

    $screen = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $lines) {
        foreach ($row in @(Split-TerminalRow -Row $line -Columns $Columns)) { $screen.Add($row) }
    }
    if ($screen.Count -gt $Rows) { $screen.RemoveRange(0, $screen.Count - $Rows) }
    $screen.ToArray()
}
