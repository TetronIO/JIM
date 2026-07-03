# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPredefinedSearchCriteriaGroup {
    <#
    .SYNOPSIS
        Gets the criteria groups (and their criteria) for a Predefined Search.

    .DESCRIPTION
        Returns the criteria groups configured on a Predefined Search. Criteria filter the
        objects a search returns. In the current release, all criteria across all groups are
        combined with AND; All/Any group logic and nested-group evaluation are honoured in a later release.

    .PARAMETER PredefinedSearchId
        The unique identifier of the Predefined Search.

    .OUTPUTS
        PSCustomObject. The criteria groups, each with their criteria.

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
    [OutputType([PSCustomObject])]
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
