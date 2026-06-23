# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMPredefinedSearchCriterion {
    <#
    .SYNOPSIS
        Removes a criterion from a Predefined Search criteria group.

    .DESCRIPTION
        Deletes a specific criterion from within a Predefined Search criteria group.

    .PARAMETER PredefinedSearchId
        The unique identifier of the Predefined Search.

    .PARAMETER GroupId
        The unique identifier of the criteria group containing the criterion.

    .PARAMETER CriterionId
        The unique identifier of the criterion to remove.

    .OUTPUTS
        None

    .EXAMPLE
        Remove-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 -CriterionId 15

        Removes the criterion with ID 15 from group 10.

    .LINK
        Get-JIMPredefinedSearchCriteriaGroup
        New-JIMPredefinedSearchCriterion
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$PredefinedSearchId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$GroupId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$CriterionId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess("Criterion $CriterionId in Group $GroupId on Predefined Search $PredefinedSearchId", "Remove")) {
            Write-Verbose "Removing criterion $CriterionId from group $GroupId in Predefined Search $PredefinedSearchId"
            try {
                Invoke-JIMApi -Endpoint "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups/$GroupId/criteria/$CriterionId" -Method 'DELETE'
                Write-Verbose "Removed criterion $CriterionId"
            }
            catch {
                Write-Error "Failed to remove criterion: $_"
            }
        }
    }
}
