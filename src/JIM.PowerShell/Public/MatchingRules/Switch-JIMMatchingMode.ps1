# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Switch-JIMMatchingMode {
    <#
    .SYNOPSIS
        Switches a Connected System between simple and advanced Object Matching Rule mode.

    .DESCRIPTION
        Switches where a Connected System's Object Matching Rules live: on its Object Types (ConnectedSystem, simple
        mode), shared by every Synchronisation Rule for the type, or on each import and export Synchronisation Rule
        (SyncRule, advanced mode).

        Switching to advanced mode copies each Object Type's rules onto its import Synchronisation Rules that have
        none of their own. The Object Types' own rules are kept but no longer consulted, and export Synchronisation
        Rules receive nothing, so export matching stops until rules are added to them. Switching to simple mode
        moves the Synchronisation Rules' rules onto their Object Types; where an Object Type already has rules,
        those stay and the Synchronisation Rules' rules are discarded. Each of these is reported as a warning.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER Mode
        The mode to switch to: ConnectedSystem (simple mode) keeps the rules on Object Types, and SyncRule (advanced
        mode) on each Synchronisation Rule.

    .PARAMETER PassThru
        Returns what the switch did.

    .OUTPUTS
        With -PassThru, what the switch did; otherwise nothing. Its warnings are written to the warning stream
        either way.

    .EXAMPLE
        Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode SyncRule

        Switches Connected System 1 to advanced mode (matching rules on Synchronisation Rules).

    .EXAMPLE
        Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode ConnectedSystem -PassThru

        Switches Connected System 1 to simple mode and returns the migration result.

    .LINK
        Get-JIMMatchingRule
        Get-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType('JIM.ObjectMatchingModeSwitchResult')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory)]
        [ValidateSet('ConnectedSystem', 'SyncRule')]
        [string]$Mode,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{
            # Send the enum as its string name; -Mode is ValidateSet-constrained to the
            # exact ObjectMatchingRuleMode member names. The API rejects numeric ordinals
            # (JsonStringEnumConverter allowIntegerValues:false, PR #1060).
            mode = $Mode
        }

        $modeDescription = if ($Mode -eq 'SyncRule') { 'Advanced (per-Synchronisation Rule)' } else { 'Simple (per-Object Type)' }

        if ($PSCmdlet.ShouldProcess("Connected System $ConnectedSystemId", "Switch matching mode to $modeDescription")) {
            Write-Verbose "Switching matching mode for Connected System ID: $ConnectedSystemId to $modeDescription"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-mode" -Method 'POST' -Body $body

                if ($result.warnings -and $result.warnings.Count -gt 0) {
                    foreach ($warning in $result.warnings) {
                        Write-Warning $warning
                    }
                }

                Write-Verbose "Switched matching mode successfully"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to switch matching mode: $_"
            }
        }
    }
}
