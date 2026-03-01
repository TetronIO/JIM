function Get-JIMMetaverseObject {
    <#
    .SYNOPSIS
        Gets Metaverse Objects from JIM.

    .DESCRIPTION
        Retrieves Metaverse Objects from JIM. Can retrieve all objects with optional
        filtering, or a specific object by ID. Supports selecting which attributes
        to include in the response.

        By default, returns a single page of results. Use -All to automatically
        paginate through all results and return every matching object.

    .PARAMETER Id
        The unique identifier (GUID) of a specific Metaverse Object to retrieve.

    .PARAMETER ObjectTypeId
        Filter objects by Metaverse Object Type ID.

    .PARAMETER ObjectTypeName
        Filter objects by Metaverse Object Type name.

    .PARAMETER Search
        Search query to filter objects by display name (supports wildcards).

    .PARAMETER AttributeName
        Filter by a specific attribute name. Must be used with AttributeValue.
        This performs an exact match (case-insensitive).

    .PARAMETER AttributeValue
        Filter by a specific attribute value. Must be used with AttributeName.
        This performs an exact match (case-insensitive).

    .PARAMETER Attributes
        List of attribute names to include in the response. Use "*" for all attributes.
        DisplayName is always included by default.

    .PARAMETER All
        Automatically paginate through all results and return every matching object.
        Cannot be used with -Page.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1. Cannot be used with -All.

    .PARAMETER PageSize
        Number of items per page. Defaults to 100. Maximum is 100.

    .OUTPUTS
        PSCustomObject representing Metaverse Object(s).

    .EXAMPLE
        Get-JIMMetaverseObject

        Gets all Metaverse Objects (first page).

    .EXAMPLE
        Get-JIMMetaverseObject -All

        Gets all Metaverse Objects, automatically paginating through all results.

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
        Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue "jsmith"

        Gets the Metaverse Object with Account Name equal to "jsmith".

    .EXAMPLE
        Get-JIMMetaverseObject -ObjectTypeName "Group" -AttributeName "Account Name" -AttributeValue "Project-Alpha"

        Gets the Group with Account Name equal to "Project-Alpha".

    .EXAMPLE
        Get-JIMMetaverseObject -Search "john*" -Attributes FirstName, LastName, Email

        Searches and includes specific attributes in the response.

    .EXAMPLE
        Get-JIMMetaverseObject -Attributes *

        Gets all objects with all attributes included.

    .EXAMPLE
        Get-JIMMetaverseObject -ObjectTypeName "User" -Attributes "Training Status" -All

        Gets all User objects with Training Status attribute, automatically paginating.

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
        [Parameter(ParameterSetName = 'ListAll')]
        [int]$ObjectTypeId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateNotNullOrEmpty()]
        [string]$ObjectTypeName,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [SupportsWildcards()]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateNotNullOrEmpty()]
        [string]$AttributeName,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string]$AttributeValue,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string[]]$Attributes,

        [Parameter(Mandatory, ParameterSetName = 'ListAll')]
        [switch]$All,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateRange(1, 100)]
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

            { $_ -in 'List', 'ListAll' } {
                Write-Verbose "Getting Metaverse Objects"

                # Validate AttributeName and AttributeValue are used together
                if ($AttributeName -and -not $PSBoundParameters.ContainsKey('AttributeValue')) {
                    Write-Error "AttributeName requires AttributeValue to be specified"
                    return
                }
                if ($PSBoundParameters.ContainsKey('AttributeValue') -and -not $AttributeName) {
                    Write-Error "AttributeValue requires AttributeName to be specified"
                    return
                }

                # Build base query parameters (excluding page, which varies during pagination)
                $baseQueryParams = @(
                    "pageSize=$PageSize"
                )

                if ($PSBoundParameters.ContainsKey('ObjectTypeId') -or $ObjectTypeName) {
                    $baseQueryParams += "objectTypeId=$ObjectTypeId"
                }

                if ($Search) {
                    $baseQueryParams += "search=$([System.Uri]::EscapeDataString($Search))"
                }

                if ($AttributeName) {
                    $baseQueryParams += "filterAttributeName=$([System.Uri]::EscapeDataString($AttributeName))"
                    $baseQueryParams += "filterAttributeValue=$([System.Uri]::EscapeDataString($AttributeValue))"
                }

                if ($Attributes) {
                    foreach ($attr in $Attributes) {
                        $baseQueryParams += "attributes=$([System.Uri]::EscapeDataString($attr))"
                    }
                }

                $currentPage = $Page
                do {
                    $queryParams = @("page=$currentPage") + $baseQueryParams
                    $queryString = $queryParams -join '&'
                    $response = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects?$queryString"

                    # Handle paginated response - check property exists, not truthy (empty array is valid)
                    $objects = if ($null -ne $response.items) { $response.items } else { $response }

                    # Output each object individually for pipeline support
                    foreach ($obj in $objects) {
                        $obj
                    }

                    # Check if we should fetch the next page
                    $hasMore = $All -and $response.hasNextPage -eq $true
                    if ($hasMore) {
                        $currentPage++
                        Write-Verbose "Fetching page $currentPage of $($response.totalPages)..."
                    }
                } while ($hasMore)
            }
        }
    }
}
