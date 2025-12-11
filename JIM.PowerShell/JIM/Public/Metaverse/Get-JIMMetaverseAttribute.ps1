function Get-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Gets Metaverse Attributes from JIM.

    .DESCRIPTION
        Retrieves Metaverse Attribute definitions from JIM. Can retrieve all attributes
        or a specific attribute by ID.

    .PARAMETER Id
        The unique identifier of a specific Attribute to retrieve.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 100.

    .OUTPUTS
        PSCustomObject representing Attribute(s).

    .EXAMPLE
        Get-JIMMetaverseAttribute

        Gets all Metaverse Attributes.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Id 1

        Gets the Attribute with ID 1.

    .LINK
        Get-JIMMetaverseObject
        Get-JIMMetaverseObjectType
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

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
                Write-Verbose "Getting Metaverse Attribute with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes/$Id"
                $result
            }

            'List' {
                Write-Verbose "Getting all Metaverse Attributes"
                $queryParams = @(
                    "page=$Page",
                    "pageSize=$PageSize"
                )
                $queryString = $queryParams -join '&'
                $response = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes?$queryString"

                # Handle paginated response
                $attributes = if ($response.items) { $response.items } else { $response }

                # Output each attribute individually for pipeline support
                foreach ($attr in $attributes) {
                    $attr
                }
            }
        }
    }
}
