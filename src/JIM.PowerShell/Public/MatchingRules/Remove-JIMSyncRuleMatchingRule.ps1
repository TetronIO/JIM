function Remove-JIMSyncRuleMatchingRule {
    <#
    .SYNOPSIS
        Removes an Object Matching Rule from a Sync Rule (advanced mode).

    .DESCRIPTION
        Deletes an Object Matching Rule from a Sync Rule.
        This operation cannot be undone.

    .PARAMETER SyncRuleId
        The unique identifier of the Sync Rule.

    .PARAMETER Id
        The unique identifier of the Matching Rule to delete.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12

        Removes Matching Rule 12 from Sync Rule 5 (with confirmation).

    .EXAMPLE
        Remove-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12 -Force

        Removes Matching Rule 12 without confirmation.

    .EXAMPLE
        Get-JIMSyncRuleMatchingRule -SyncRuleId 5 | Remove-JIMSyncRuleMatchingRule -Force

        Removes all Matching Rules from Sync Rule 5 without confirmation.

    .LINK
        Get-JIMSyncRuleMatchingRule
        New-JIMSyncRuleMatchingRule
        Set-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $shouldProcess = $Force -or $PSCmdlet.ShouldProcess("Matching Rule $Id on Sync Rule $SyncRuleId", "Remove")

        if ($shouldProcess) {
            Write-Verbose "Removing Matching Rule ID: $Id from Sync Rule ID: $SyncRuleId"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/matching-rules/$Id" -Method 'DELETE'

                Write-Verbose "Removed Matching Rule ID: $Id"
            }
            catch {
                Write-Error "Failed to remove Matching Rule: $_"
            }
        }
    }
}
