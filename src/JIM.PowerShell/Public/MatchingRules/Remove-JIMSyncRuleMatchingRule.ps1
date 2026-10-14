# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSyncRuleMatchingRule {
    <#
    .SYNOPSIS
        Deletes an Object Matching Rule from a Synchronisation Rule (advanced mode).

    .DESCRIPTION
        Deletes an Object Matching Rule from a Synchronisation Rule.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER Id
        The ID of the rule to delete.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12

        Removes Matching Rule 12 from Synchronisation Rule 5 (with confirmation).

    .EXAMPLE
        Remove-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12 -Force

        Removes Matching Rule 12 without confirmation.

    .EXAMPLE
        Get-JIMSyncRuleMatchingRule -SyncRuleId 5 | Remove-JIMSyncRuleMatchingRule -Force

        Removes all Matching Rules from Synchronisation Rule 5 without confirmation.

    .LINK
        Get-JIMSyncRuleMatchingRule
        New-JIMSyncRuleMatchingRule
        Set-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([void])]
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
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $shouldProcess = Confirm-JIMAction -Cmdlet $PSCmdlet -Target "Matching Rule $Id on Synchronisation Rule $SyncRuleId" -Action "Remove" -Force:$Force

        if ($shouldProcess) {
            Write-Verbose "Removing Matching Rule ID: $Id from Synchronisation Rule ID: $SyncRuleId"

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
