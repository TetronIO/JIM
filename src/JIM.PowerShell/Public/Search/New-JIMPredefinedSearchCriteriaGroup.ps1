# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMPredefinedSearchCriteriaGroup {
    <#
    .SYNOPSIS
        Creates a new criteria group on a Predefined Search.

    .DESCRIPTION
        Creates a criteria group on a Predefined Search, either top-level or nested under an existing
        group. The group type determines how criteria within it are evaluated:
        - All: All criteria must match (AND logic)
        - Any: At least one criterion must match (OR logic)

        Within a group, criteria and nested child groups are combined per the group's type; top-level
        groups are combined with OR. Use -ParentGroupId to nest a group, for example to express
        (A OR B) AND C.

    .PARAMETER PredefinedSearchId
        The unique identifier of the Predefined Search.

    .PARAMETER ParentGroupId
        Optional. The ID of an existing group to nest this group within. If omitted, creates a top-level group.

    .PARAMETER Type
        The logical operator for this group: 'All' (AND) or 'Any' (OR). Defaults to 'All'.

    .PARAMETER Position
        Optional position/order for this group. Defaults to 0.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the owning Predefined
        Search's configuration change history.

    .PARAMETER PassThru
        If specified, returns the created group object.

    .OUTPUTS
        If -PassThru is specified, returns the created criteria group.

    .EXAMPLE
        New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -Type All

        Creates a top-level criteria group with AND logic.

    .EXAMPLE
        New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -ParentGroupId 10 -Type Any -PassThru

        Creates a child group with OR logic nested under group 10.

    .LINK
        Get-JIMPredefinedSearchCriteriaGroup
        Set-JIMPredefinedSearchCriteriaGroup
        Remove-JIMPredefinedSearchCriteriaGroup
        New-JIMPredefinedSearchCriterion
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$PredefinedSearchId,

        [Parameter()]
        [int]$ParentGroupId,

        [Parameter()]
        [ValidateSet('All', 'Any')]
        [string]$Type = 'All',

        [Parameter()]
        [int]$Position = 0,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{
            type = $Type
            position = $Position
        }
        if ($ChangeReason) {
            $body.changeReason = $ChangeReason
        }

        $endpoint = if ($PSBoundParameters.ContainsKey('ParentGroupId')) {
            "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups/$ParentGroupId/child-groups"
        }
        else {
            "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups"
        }

        $target = if ($PSBoundParameters.ContainsKey('ParentGroupId')) {
            "Predefined Search $PredefinedSearchId (under group $ParentGroupId)"
        }
        else {
            "Predefined Search $PredefinedSearchId"
        }

        if ($PSCmdlet.ShouldProcess($target, "Create Criteria Group ($Type)")) {
            Write-Verbose "Creating criteria group for Predefined Search $PredefinedSearchId"
            try {
                $result = Invoke-JIMApi -Endpoint $endpoint -Method 'POST' -Body $body
                Write-Verbose "Created criteria group ID: $($result.id)"
                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'PredefinedSearchId' -NotePropertyValue $PredefinedSearchId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create criteria group: $_"
            }
        }
    }
}
