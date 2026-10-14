# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMScopingCriteriaGroup {
    <#
    .SYNOPSIS
        Deletes a scoping criteria group and everything in it.

    .DESCRIPTION
        Deletes a scoping criteria group, with every criterion and child group inside it.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER GroupId
        The ID of the group to delete.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 10

        Removes the scoping criteria group with ID 10.

    .EXAMPLE
        Get-JIMScopingCriteria -SyncRuleId 5 | Remove-JIMScopingCriteriaGroup

        Removes all scoping criteria groups from Synchronisation Rule 5.

    .LINK
        Get-JIMScopingCriteria
        New-JIMScopingCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([void])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$GroupId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess("Scoping Criteria Group $GroupId", "Remove")) {
            Write-Verbose "Removing scoping criteria group $GroupId from Synchronisation Rule $SyncRuleId"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$GroupId" -Method 'DELETE'

                Write-Verbose "Removed scoping criteria group $GroupId"
            }
            catch {
                Write-Error "Failed to remove scoping criteria group: $_"
            }
        }
    }
}
