# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMetaverseObjectType {
    <#
    .SYNOPSIS
        Gets Metaverse Object Types from JIM.

    .DESCRIPTION
        Retrieves Metaverse Object Type definitions from JIM. Can retrieve all types
        or a specific type by ID or name.

    .PARAMETER Id
        The ID of the Object Type to get.

    .PARAMETER Name
        The name of the Object Type to get.

    .PARAMETER IncludeChildObjects
        Loads each Object Type's attributes. In the list, AttributesCount then counts them; with -Id or -Name,
        Attributes lists them. Without it, AttributesCount is 0 and Attributes is empty.

    .PARAMETER Page
        The page of Object Types to return, counting from 1.

    .PARAMETER PageSize
        How many Object Types each page holds.

    .OUTPUTS
        By default, one page of Object Types, each in summary. With -Id or -Name, the Object Type in full, with its
        deletion settings and any warning about them.

    .EXAMPLE
        Get-JIMMetaverseObjectType

        Gets all Metaverse Object Types.

    .EXAMPLE
        Get-JIMMetaverseObjectType -Id 1

        Gets the Object Type with ID 1.

    .EXAMPLE
        if ((Get-JIMMetaverseObjectType -Name 'User').DeletionSourceWarnings) { throw 'A projecting system is not an authoritative source for User' }

        Fails a health-check script when a Connected System projects into the User type without being one of its
        authoritative sources.

    .EXAMPLE
        Get-JIMMetaverseObjectType -Name 'Person'

        Gets the Object Type named 'Person'.

    .EXAMPLE
        Get-JIMMetaverseObjectType -Id 1 -IncludeChildObjects

        Gets Object Type ID 1 with child object counts.

    .LINK
        Get-JIMMetaverseObject
        Get-JIMMetaverseAttribute
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.MetaverseObjectTypeHeader', ParameterSetName = 'List')]
    [OutputType('JIM.MetaverseObjectTypeDetailDto', ParameterSetName = 'ById')]
    [OutputType('JIM.MetaverseObjectTypeDetailDto', ParameterSetName = 'ByName')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [switch]$IncludeChildObjects,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 1000)]
        [int]$PageSize = 100
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Resolve name to ID if using ByName parameter set
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $resolvedType = Resolve-JIMMetaverseObjectType -Name $Name
                $Id = $resolvedType.id
            }
            catch {
                Write-Error $_
                return
            }
        }

        switch ($PSCmdlet.ParameterSetName) {
            { $_ -in 'ById', 'ByName' } {
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
