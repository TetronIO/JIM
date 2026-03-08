function Switch-JIMMatchingMode {
    <#
    .SYNOPSIS
        Switches the object matching rule mode for a Connected System.

    .DESCRIPTION
        Switches between simple mode (matching rules on object types) and advanced mode
        (matching rules on sync rules) for a Connected System.

        When switching to advanced mode, matching rules are copied from object types to sync rules.
        When switching to simple mode, matching rules are migrated from sync rules to object types.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER Mode
        The new object matching rule mode.
        Valid values: 'ConnectedSystem' (simple mode) or 'SyncRule' (advanced mode).

    .PARAMETER PassThru
        If specified, returns the mode switch result object.

    .OUTPUTS
        If -PassThru is specified, returns the mode switch result object containing
        details about what was migrated.

    .EXAMPLE
        Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode SyncRule

        Switches Connected System 1 to advanced mode (matching rules on sync rules).

    .EXAMPLE
        Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode ConnectedSystem -PassThru

        Switches Connected System 1 to simple mode and returns the migration result.

    .LINK
        Get-JIMMatchingRule
        Get-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([PSCustomObject])]
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
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Map string to numeric mode value
        $modeValue = switch ($Mode) {
            'ConnectedSystem' { 0 }
            'SyncRule' { 1 }
        }

        $body = @{
            mode = $modeValue
        }

        $modeDescription = if ($Mode -eq 'SyncRule') { 'Advanced (per-Sync Rule)' } else { 'Simple (per-Object Type)' }

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
