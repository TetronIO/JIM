# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMPredefinedSearchCriteriaGroup {
    <#
    .SYNOPSIS
        Updates a criteria group's logic type or position on a Predefined Search.

    .DESCRIPTION
        Updates the All/Any logic type and/or position of an existing criteria group.

    .PARAMETER PredefinedSearchId
        The ID of the Predefined Search.

    .PARAMETER GroupId
        The ID of the criteria group to update.

    .PARAMETER Type
        How the group combines what it holds: All (AND) or Any (OR).

    .PARAMETER Position
        The group's place among its siblings.

    .PARAMETER ChangeReason
        Why the change was made, recorded on the audit Activity and shown in the Predefined Search's Configuration
        Change History.

    .PARAMETER PassThru
        Returns the updated group.

    .OUTPUTS
        With -PassThru, the updated group, with a PredefinedSearchId property added naming its search; otherwise
        nothing.

    .EXAMPLE
        Set-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -GroupId 10 -Type Any

        Changes group 10 to use OR logic.

    .LINK
        Get-JIMPredefinedSearchCriteriaGroup
        New-JIMPredefinedSearchCriteriaGroup
        Remove-JIMPredefinedSearchCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.PredefinedSearchCriteriaGroupDto')]
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
