# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMPredefinedSearchCriteriaGroup {
    <#
    .SYNOPSIS
        Creates a new criteria group on a Predefined Search.

    .DESCRIPTION
        Creates a top-level criteria group on a Predefined Search. The group type determines how
        criteria within it are evaluated:
        - All: All criteria must match (AND logic)
        - Any: At least one criterion must match (OR logic)

        Note: in the current release, criteria are combined with AND across groups; All/Any group
        logic and nested groups are honoured in a later release.

    .PARAMETER PredefinedSearchId
        The unique identifier of the Predefined Search.

    .PARAMETER Type
        The logical operator for this group: 'All' (AND) or 'Any' (OR). Defaults to 'All'.

    .PARAMETER Position
        Optional position/order for this group. Defaults to 0.

    .PARAMETER PassThru
        If specified, returns the created group object.

    .OUTPUTS
        If -PassThru is specified, returns the created criteria group.

    .EXAMPLE
        New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -Type All

        Creates a top-level criteria group with AND logic.

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
        [ValidateSet('All', 'Any')]
        [string]$Type = 'All',

        [Parameter()]
        [int]$Position = 0,

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

        if ($PSCmdlet.ShouldProcess("Predefined Search $PredefinedSearchId", "Create Criteria Group ($Type)")) {
            Write-Verbose "Creating criteria group for Predefined Search $PredefinedSearchId"
            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups" -Method 'POST' -Body $body
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
