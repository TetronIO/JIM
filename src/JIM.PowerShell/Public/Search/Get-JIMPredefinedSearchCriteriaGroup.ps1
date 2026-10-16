# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPredefinedSearchCriteriaGroup {
    <#
    .SYNOPSIS
        Gets the criteria groups (and their criteria) for a Predefined Search.

    .DESCRIPTION
        Returns the criteria groups configured on a Predefined Search, each with its criteria and nested groups.
        Criteria filter the objects a search returns: each group is All (AND) or Any (OR), groups nest to any depth,
        and top-level groups are combined with OR.

    .PARAMETER PredefinedSearchId
        The ID of the Predefined Search.

    .OUTPUTS
        One object per top-level criteria group, with its criteria and nested groups.

    .EXAMPLE
        Get-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3

        Returns the criteria groups for Predefined Search 3.

    .EXAMPLE
        Get-JIMPredefinedSearch -Uri 'distribution' | Get-JIMPredefinedSearchCriteriaGroup

        Pipes a Predefined Search into the cmdlet to list its criteria groups.

    .LINK
        Get-JIMPredefinedSearch
        New-JIMPredefinedSearchCriteriaGroup
        New-JIMPredefinedSearchCriterion
    #>
    [CmdletBinding()]
    [OutputType('JIM.PredefinedSearchCriteriaGroupDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$PredefinedSearchId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting criteria groups for Predefined Search $PredefinedSearchId"
        try {
            Invoke-JIMApi -Endpoint "/api/v1/predefined-searches/$PredefinedSearchId/criteria-groups"
        }
        catch {
            if ($_.Exception.Message -like '*not found*') { return }
            Write-Error "Failed to get criteria groups for Predefined Search '$PredefinedSearchId': $_"
        }
    }
}
