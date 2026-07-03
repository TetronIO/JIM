# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMLogLevelColour {
    <#
    .SYNOPSIS
        Internal function to map a log level name to a console colour.

    .DESCRIPTION
        Returns the console colour Watch-JIMLog uses to display a log entry of the given
        level: red for Error and Fatal, yellow for Warning, dark grey for Debug and
        Verbose, and no colour (the console default) for Information and anything else.

    .PARAMETER Level
        The log level name (Verbose, Debug, Information, Warning, Error, Fatal).

    .OUTPUTS
        The console colour name as a string, or $null for the console default.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Level
    )

    switch ($Level) {
        'Fatal' { 'Red' }
        'Error' { 'Red' }
        'Warning' { 'Yellow' }
        'Debug' { 'DarkGray' }
        'Verbose' { 'DarkGray' }
        default { $null }
    }
}
