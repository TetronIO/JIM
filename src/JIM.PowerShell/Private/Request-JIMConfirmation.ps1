# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Request-JIMConfirmation {
    <#
    .SYNOPSIS
        Internal function asking the person running a cmdlet to confirm a decision only they can make.

    .DESCRIPTION
        A thin wrapper over ShouldContinue, which always asks, whatever $ConfirmPreference says: unlike ShouldProcess,
        it is for a decision that needs a person, such as checking a certificate's thumbprint before trusting it, or
        accepting that a change stops everyone signing in. Callers skip it under -Force, and decide -WhatIf with
        ShouldProcess first. A function of its own so tests can answer it.

    .PARAMETER Cmdlet
        The calling cmdlet's $PSCmdlet.

    .PARAMETER Query
        What is being asked, with everything needed to decide.

    .PARAMETER Caption
        The prompt's heading.

    .OUTPUTS
        $true if the person agreed; otherwise $false.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.PSCmdlet]$Cmdlet,

        [Parameter(Mandatory)]
        [string]$Query,

        [Parameter(Mandatory)]
        [string]$Caption
    )

    $Cmdlet.ShouldContinue($Query, $Caption)
}
