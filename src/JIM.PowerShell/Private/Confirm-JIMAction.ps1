# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Confirm-JIMAction {
    <#
    .SYNOPSIS
        Internal function deciding whether a cmdlet that supports -WhatIf, -Confirm and -Force should act.

    .DESCRIPTION
        -Force answers the confirmation prompt; it never overrides -WhatIf, which must only ever preview.

        A cmdlet that writes "if ($Force -or $PSCmdlet.ShouldProcess(...))" never consults ShouldProcess once
        -Force is given, so "-WhatIf -Force" carries the action out instead of describing it: on Reset-JIMSystem,
        that is a factory reset. This function is the one place a cmdlet combines the two. Under -WhatIf, or a
        session-wide $WhatIfPreference, it returns ShouldProcess's answer, which writes the "What if" line and is
        false. Otherwise -Force returns true without prompting, and without -Force ShouldProcess decides as usual.

    .PARAMETER Cmdlet
        The calling cmdlet's $PSCmdlet.

    .PARAMETER Target
        What the action is performed on, as ShouldProcess names it.

    .PARAMETER Action
        What is done to it, as ShouldProcess names it.

    .PARAMETER Force
        The calling cmdlet's -Force.

    .OUTPUTS
        $true if the cmdlet should carry the action out; otherwise $false.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.PSCmdlet]$Cmdlet,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Target,

        [Parameter(Mandatory)]
        [string]$Action,

        [switch]$Force
    )

    # Read in the caller's scope, where -WhatIf (or a session-wide preference) set it.
    $whatIf = [bool]$Cmdlet.GetVariableValue('WhatIfPreference')
    if ($Force -and -not $whatIf) {
        return $true
    }
    $Cmdlet.ShouldProcess($Target, $Action)
}
