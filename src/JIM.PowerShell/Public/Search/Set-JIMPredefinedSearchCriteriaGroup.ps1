# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMPredefinedSearchCriteriaGroup {
    <#
    .SYNOPSIS
        Updates a criteria group's logic type or position on a Predefined Search.

    .DESCRIPTION
        Updates the All/Any logic type and/or position of an existing criteria group.

    .PARAMETER PredefinedSearchId
        The unique identifier of the Predefined Search.

    .PARAMETER GroupId
        The unique identifier of the criteria group to update.

    .PARAMETER Type
        The logical operator for this group: 'All' (AND) or 'Any' (OR).

    .PARAMETER Position
        The position/order for this group.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the owning Predefined
        Search's configuration change history.

    .PARAMETER PassThru
        If specified, returns the updated group object.

    .OUTPUTS
        If -PassThru is specified, returns the updated criteria group.

    .EXAMPLE
        Set-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -GroupId 10 -Type Any

        Changes group 10 to use OR logic.

    .LINK
        Get-JIMPredefinedSearchCriteriaGroup
        New-JIMPredefinedSearchCriteriaGroup
        Remove-JIMPredefinedSearchCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$PredefinedSearchId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$GroupId,

        [Parameter()]
        [ValidateSet('All', 'Any')]
        [string]$Type,

        [Parameter()]
        [int]$Position,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{}
        if ($PSBoundParameters.ContainsKey('Type')) { $body.type = $Type }
        if ($PSBoundParameters.ContainsKey('Position')) { $body.position = $Position }
        if ($ChangeReason) { $body.changeReason = $ChangeReason }

        if ($PSCmdlet.ShouldProcess("Criteria Group $GroupId on Predefined Search $PredefinedSearchId", "Update")) {
            Write-Verbose "Updating criteria group $GroupId on Predefined Search $PredefinedSearchId"
            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups/$GroupId" -Method 'PUT' -Body $body
                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'PredefinedSearchId' -NotePropertyValue $PredefinedSearchId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to update criteria group: $_"
            }
        }
    }
}
