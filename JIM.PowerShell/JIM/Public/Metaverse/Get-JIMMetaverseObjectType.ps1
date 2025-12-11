function Get-JIMMetaverseObjectType {
    <#
    .SYNOPSIS
        Gets Metaverse Object Types from JIM.

    .DESCRIPTION
        Retrieves Metaverse Object Type definitions from JIM. Can retrieve all types
        or a specific type by ID.

    .PARAMETER Id
        The unique identifier of a specific Object Type to retrieve.

    .PARAMETER IncludeChildObjects
        If specified, includes child object counts in the response.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 100.

    .OUTPUTS
        PSCustomObject representing Object Type(s).

    .EXAMPLE
        Get-JIMMetaverseObjectType

        Gets all Metaverse Object Types.

    .EXAMPLE
        Get-JIMMetaverseObjectType -Id 1

        Gets the Object Type with ID 1.

    .EXAMPLE
        Get-JIMMetaverseObjectType -Id 1 -IncludeChildObjects

        Gets Object Type ID 1 with child object counts.

    .LINK
        Get-JIMMetaverseObject
        Get-JIMMetaverseAttribute
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [switch]$IncludeChildObjects,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 1000)]
        [int]$PageSize = 100
    )

    process {
        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Metaverse Object Type with ID: $Id"
                $queryParams = @()
                if ($IncludeChildObjects) {
                    $queryParams += "includeChildObjects=true"
                }
                $queryString = if ($queryParams) { "?$($queryParams -join '&')" } else { "" }
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/object-types/$Id$queryString"
                $result
            }

            'List' {
                Write-Verbose "Getting all Metaverse Object Types"
                $queryParams = @(
                    "page=$Page",
                    "pageSize=$PageSize"
                )
                if ($IncludeChildObjects) {
                    $queryParams += "includeChildObjects=true"
                }
                $queryString = $queryParams -join '&'
                $response = Invoke-JIMApi -Endpoint "/api/v1/metaverse/object-types?$queryString"

                # Handle paginated response
                $types = if ($response.items) { $response.items } else { $response }

                # Output each type individually for pipeline support
                foreach ($type in $types) {
                    $type
                }
            }
        }
    }
}
