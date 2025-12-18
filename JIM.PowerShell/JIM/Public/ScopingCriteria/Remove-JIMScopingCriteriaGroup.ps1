function Remove-JIMScopingCriteriaGroup {
    <#
    .SYNOPSIS
        Removes a scoping criteria group from a sync rule.

    .DESCRIPTION
        Deletes a scoping criteria group and all its contents (criteria and child groups).

    .PARAMETER SyncRuleId
        The unique identifier of the sync rule.

    .PARAMETER GroupId
        The unique identifier of the criteria group to remove.

    .OUTPUTS
        None

    .EXAMPLE
        Remove-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 10

        Removes the scoping criteria group with ID 10.

    .EXAMPLE
        Get-JIMScopingCriteria -SyncRuleId 5 | Remove-JIMScopingCriteriaGroup

        Removes all scoping criteria groups from sync rule 5.

    .LINK
        Get-JIMScopingCriteria
        New-JIMScopingCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$GroupId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ShouldProcess("Scoping Criteria Group $GroupId", "Remove")) {
            Write-Verbose "Removing scoping criteria group $GroupId from sync rule $SyncRuleId"

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
