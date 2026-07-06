# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMPredefinedSearchCriteriaGroup {
    <#
    .SYNOPSIS
        Removes a criteria group (and its contents) from a Predefined Search.

    .DESCRIPTION
        Deletes a criteria group and its entire subtree (nested groups and all contained criteria)
        from a Predefined Search.

    .PARAMETER PredefinedSearchId
        The unique identifier of the Predefined Search.

    .PARAMETER GroupId
        The unique identifier of the criteria group to remove.

    .PARAMETER ChangeReason
        Optional reason for the removal, recorded on the audit Activity and shown in the owning Predefined
        Search's configuration change history.

    .OUTPUTS
        None

    .EXAMPLE
        Remove-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -GroupId 10

        Removes criteria group 10 and everything within it.

    .EXAMPLE
        Remove-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -GroupId 10 -ChangeReason "Consolidating filters (CHG0129)"

        Removes the group and records the reason on its configuration change history.

    .LINK
        Get-JIMPredefinedSearchCriteriaGroup
        New-JIMPredefinedSearchCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$PredefinedSearchId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$GroupId,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess("Criteria Group $GroupId on Predefined Search $PredefinedSearchId", "Remove")) {
            Write-Verbose "Removing criteria group $GroupId from Predefined Search $PredefinedSearchId"
            try {
                $endpoint = "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups/$GroupId"
                if ($ChangeReason) {
                    $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                Invoke-JIMApi -Endpoint $endpoint -Method 'DELETE'
                Write-Verbose "Removed criteria group $GroupId"
            }
            catch {
                Write-Error "Failed to remove criteria group: $_"
            }
        }
    }
}
