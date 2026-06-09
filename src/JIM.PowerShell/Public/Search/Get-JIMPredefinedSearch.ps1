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

        When -Id or a literal -Uri is supplied the cmdlet calls a dedicated server endpoint
        and returns the full search graph (attributes and criteria). Wildcard -Uri patterns
        and the unfiltered list view return lightweight headers.

    .PARAMETER Id
        Return only the search with this ID. Resolves to a single full search via the server.

    .PARAMETER Uri
        Return only the search with this URI (the stable, human-readable slug such as
        "people" or "security-groups"). Supports wildcards. A literal URI resolves to a
        single full search via the server; a wildcard pattern is filtered client-side
        against the list of headers.

    .OUTPUTS
        PSCustomObject. For -Id and literal -Uri lookups, the full Predefined Search
        including its attributes and criteria. Otherwise, Predefined Search headers.

    .EXAMPLE
        Get-JIMPredefinedSearch

        Lists all Predefined Searches as headers, including any that are disabled.

    .EXAMPLE
        Get-JIMPredefinedSearch -Uri 'people'

        Returns the full Predefined Search identified by the URI 'people'.

    .EXAMPLE
        Get-JIMPredefinedSearch -Uri 'sec*'

        Returns headers for all Predefined Searches whose URI starts with "sec".

    .EXAMPLE
        Get-JIMPredefinedSearch -Id 3

        Returns the full Predefined Search with ID 3.

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
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Predefined Search by ID $Id"
                try {
                    Invoke-JIMApi -Endpoint "/api/v1/predefined-searches/$Id"
                }
                catch {
                    if ($_.Exception.Message -like '*not found*') { return }
                    Write-Error "Failed to get Predefined Search with ID '$Id': $_"
                }
                return
            }

            'ByUri' {
                if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($Uri)) {
                    Write-Verbose "Listing Predefined Searches and filtering by wildcard URI '$Uri'"
                    try {
                        $response = Invoke-JIMApi -Endpoint "/api/v1/predefined-searches"
                    }
                    catch {
                        Write-Error "Failed to list Predefined Searches: $_"
                        return
                    }
                    $items = if ($null -ne $response.items) { $response.items } else { $response }
                    $items | Where-Object { $_.uri -like $Uri }
                    return
                }

                Write-Verbose "Getting Predefined Search by URI '$Uri'"
                try {
                    $encoded = [System.Uri]::EscapeDataString($Uri)
                    Invoke-JIMApi -Endpoint "/api/v1/predefined-searches/by-uri/$encoded"
                }
                catch {
                    if ($_.Exception.Message -like '*not found*') { return }
                    Write-Error "Failed to get Predefined Search with URI '$Uri': $_"
                }
                return
            }

            default {
                Write-Verbose "Listing Predefined Searches"
                try {
                    $response = Invoke-JIMApi -Endpoint "/api/v1/predefined-searches"
                }
                catch {
                    Write-Error "Failed to list Predefined Searches: $_"
                    return
                }
                if ($null -ne $response.items) { $response.items } else { $response }
            }
        }
    }
}
