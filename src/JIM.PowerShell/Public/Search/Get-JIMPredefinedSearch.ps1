# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPredefinedSearch {
    <#
    .SYNOPSIS
        Gets Predefined Searches from JIM.

    .DESCRIPTION
        Lists the Predefined Searches configured in JIM. Administrators see all searches,
        including those that are currently disabled, so that they can be enabled or updated
        via Set-JIMPredefinedSearch.

    .PARAMETER Id
        Return only the search with this ID.

    .PARAMETER Uri
        Return only the search with this URI (the stable, human-readable slug such as
        "people" or "security-groups"). Supports wildcards.

    .OUTPUTS
        PSCustomObject representing a Predefined Search header.

    .EXAMPLE
        Get-JIMPredefinedSearch

        Lists all Predefined Searches, including any that are disabled.

    .EXAMPLE
        Get-JIMPredefinedSearch -Uri 'people'

        Returns the Predefined Search identified by the URI 'people'.

    .EXAMPLE
        Get-JIMPredefinedSearch -Id 3

        Returns the Predefined Search with ID 3.

    .LINK
        Set-JIMPredefinedSearch
        Search-JIMMetaverseObject
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByUri', ValueFromPipelineByPropertyName)]
        [SupportsWildcards()]
        [ValidateNotNullOrEmpty()]
        [string]$Uri
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        Write-Verbose "Listing Predefined Searches"

        try {
            $response = Invoke-JIMApi -Endpoint "/api/v1/predefined-searches"
        }
        catch {
            Write-Error "Failed to list Predefined Searches: $_"
            return
        }

        $items = if ($null -ne $response.items) { $response.items } else { $response }

        switch ($PSCmdlet.ParameterSetName) {
            'ById'  { $items | Where-Object { $_.id -eq $Id } }
            'ByUri' { $items | Where-Object { $_.uri -like $Uri } }
            default { $items }
        }
    }
}
