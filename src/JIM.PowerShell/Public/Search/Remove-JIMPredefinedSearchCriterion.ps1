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

    .PARAMETER ChangeReason
        Optional reason for the removal, recorded on the audit Activity and shown in the owning Predefined
        Search's configuration change history.

    .OUTPUTS
        None

    .EXAMPLE
        Remove-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 -CriterionId 15

        Removes the criterion with ID 15 from group 10.

    .EXAMPLE
        Remove-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId 10 -CriterionId 15 -ChangeReason "No longer required (CHG0130)"

        Removes the criterion and records the reason on its owning search's configuration change history.

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
        [int]$CriterionId,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess("Criterion $CriterionId in Group $GroupId on Predefined Search $PredefinedSearchId", "Remove")) {
            Write-Verbose "Removing criterion $CriterionId from group $GroupId in Predefined Search $PredefinedSearchId"
            try {
                $endpoint = "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups/$GroupId/criteria/$CriterionId"
                if ($ChangeReason) {
                    $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                Invoke-JIMApi -Endpoint $endpoint -Method 'DELETE'
                Write-Verbose "Removed criterion $CriterionId"
            }
            catch {
                Write-Error "Failed to remove criterion: $_"
            }
        }
    }
}
