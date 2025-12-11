function Get-JIMMetaverseObject {
    <#
    .SYNOPSIS
        Gets Metaverse Objects from JIM.

    .DESCRIPTION
        Retrieves Metaverse Objects from JIM. Can retrieve all objects with optional
        filtering, or a specific object by ID. Supports selecting which attributes
        to include in the response.

    .PARAMETER Id
        The unique identifier (GUID) of a specific Metaverse Object to retrieve.

    .PARAMETER ObjectTypeId
        Filter objects by Metaverse Object Type ID.

    .PARAMETER ObjectTypeName
        Filter objects by Metaverse Object Type name.

    .PARAMETER Search
        Search query to filter objects by display name (supports wildcards).

    .PARAMETER Attributes
        List of attribute names to include in the response. Use "*" for all attributes.
        DisplayName is always included by default.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 100.

    .OUTPUTS
        PSCustomObject representing Metaverse Object(s).

    .EXAMPLE
        Get-JIMMetaverseObject

        Gets all Metaverse Objects (first page).

    .EXAMPLE
        Get-JIMMetaverseObject -Id "12345678-1234-1234-1234-123456789abc"

        Gets a specific Metaverse Object by ID.

    .EXAMPLE
        Get-JIMMetaverseObject -ObjectTypeId 1

        Gets all Metaverse Objects of type ID 1.

    .EXAMPLE
        Get-JIMMetaverseObject -ObjectTypeName 'Person'

        Gets all Metaverse Objects of type 'Person'.

    .EXAMPLE
        Get-JIMMetaverseObject -Search "john*"

        Searches for objects with display name matching "john*".

    .EXAMPLE
        Get-JIMMetaverseObject -Search "john*" -Attributes FirstName, LastName, Email

        Searches and includes specific attributes in the response.

    .EXAMPLE
        Get-JIMMetaverseObject -Attributes *

        Gets all objects with all attributes included.

    .LINK
        Get-JIMMetaverseObjectType
        Get-JIMMetaverseAttribute
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [int]$ObjectTypeId,

        [Parameter(ParameterSetName = 'List')]
        [ValidateNotNullOrEmpty()]
        [string]$ObjectTypeName,

        [Parameter(ParameterSetName = 'List')]
        [SupportsWildcards()]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [string[]]$Attributes,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 1000)]
        [int]$PageSize = 100
    )

    process {
        # Resolve ObjectTypeName to ObjectTypeId if provided
        if ($ObjectTypeName) {
            try {
                $resolvedType = Resolve-JIMMetaverseObjectType -Name $ObjectTypeName
                $ObjectTypeId = $resolvedType.id
            }
            catch {
                Write-Error $_
                return
            }
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Metaverse Object with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id"
                $result
            }

            'List' {
                Write-Verbose "Getting Metaverse Objects"

                $queryParams = @(
                    "page=$Page",
                    "pageSize=$PageSize"
                )

                if ($PSBoundParameters.ContainsKey('ObjectTypeId') -or $ObjectTypeName) {
                    $queryParams += "objectTypeId=$ObjectTypeId"
                }

                if ($Search) {
                    $queryParams += "search=$([System.Uri]::EscapeDataString($Search))"
                }

                if ($Attributes) {
                    foreach ($attr in $Attributes) {
                        $queryParams += "attributes=$([System.Uri]::EscapeDataString($attr))"
                    }
                }

                $queryString = $queryParams -join '&'
                $response = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects?$queryString"

                # Handle paginated response
                $objects = if ($response.items) { $response.items } else { $response }

                # Output each object individually for pipeline support
                foreach ($obj in $objects) {
                    $obj
                }
            }
        }
    }
}
