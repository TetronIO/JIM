function Remove-JIMScopingCriterion {
    <#
    .SYNOPSIS
        Removes a criterion from a scoping criteria group.

    .DESCRIPTION
        Deletes a specific criterion from within a scoping criteria group.

    .PARAMETER SyncRuleId
        The unique identifier of the sync rule.

    .PARAMETER GroupId
        The unique identifier of the criteria group containing the criterion.

    .PARAMETER CriterionId
        The unique identifier of the criterion to remove.

    .OUTPUTS
        None

    .EXAMPLE
        Remove-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -CriterionId 15

        Removes the criterion with ID 15 from group 10.

    .LINK
        Get-JIMScopingCriteria
        New-JIMScopingCriterion
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$GroupId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$CriterionId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ShouldProcess("Criterion $CriterionId in Group $GroupId", "Remove")) {
            Write-Verbose "Removing criterion $CriterionId from group $GroupId in sync rule $SyncRuleId"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$GroupId/criteria/$CriterionId" -Method 'DELETE'

                Write-Verbose "Removed criterion $CriterionId"
            }
            catch {
                Write-Error "Failed to remove criterion: $_"
            }
        }
    }
}
