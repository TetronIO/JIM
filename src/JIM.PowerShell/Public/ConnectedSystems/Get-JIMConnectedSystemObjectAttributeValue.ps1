# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectAttributeValue {
    <#
    .SYNOPSIS
        Gets a Connected System Object's values of one attribute.

    .DESCRIPTION
        Gets the values of one attribute on a Connected System Object a page at a time: for a multi-valued
        attribute, such as member, that can hold thousands.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER CsoId
        The ID of the Connected System Object.

    .PARAMETER AttributeName
        The name of the attribute to get the values of.

    .PARAMETER Search
        Returns only the values that match this text.

    .PARAMETER Page
        The page to get.

    .PARAMETER PageSize
        How many values a page holds.

    .PARAMETER All
        Fetches every page instead of one. Stops after 1000 pages with a warning; -Force fetches the rest.

    .PARAMETER Force
        With -All, fetches every page, past the 1000-page limit.

    .OUTPUTS
        The attribute's values.

    .EXAMPLE
        Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 1 -CsoId "a1b2c3d4-..." -AttributeName "memberOf"

        Gets the first page of memberOf attribute values for the specified connector space object.

    .EXAMPLE
        Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 1 -CsoId "a1b2c3d4-..." -AttributeName "member" -Search "Engineering"

        Gets attribute values for the member attribute, filtered to entries containing "Engineering".

    .EXAMPLE
        Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 1 -CsoId "a1b2c3d4-..." -AttributeName "memberOf" -All

        Gets all memberOf attribute values across all pages (auto-paginates).

    .EXAMPLE
        Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 1 -CsoId "a1b2c3d4-..." -AttributeName "memberOf" -All -Force

        Gets all memberOf attribute values, overriding the 1000-page safety cap for a very large group.

    .LINK
        Get-JIMConnectedSystemObject
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType('JIM.ConnectedSystemObjectAttributeValueDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Page')]
        [Parameter(Mandatory, ParameterSetName = 'All')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'Page')]
        [Parameter(Mandatory, ParameterSetName = 'All')]
        [guid]$CsoId,

        [Parameter(Mandatory, ParameterSetName = 'Page')]
        [Parameter(Mandatory, ParameterSetName = 'All')]
        [ValidateNotNullOrEmpty()]
        [string]$AttributeName,

        [Parameter(ParameterSetName = 'Page')]
        [Parameter(ParameterSetName = 'All')]
        [string]$Search,

        [Parameter(ParameterSetName = 'Page')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'Page')]
        [Parameter(ParameterSetName = 'All')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All,

        [Parameter(ParameterSetName = 'All')]
        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $encodedAttributeName = [System.Uri]::EscapeDataString($AttributeName)

        switch ($PSCmdlet.ParameterSetName) {
            'Page' {
                Write-Verbose "Getting attribute values for '$AttributeName' on connector space object $CsoId in Connected System $ConnectedSystemId (Page: $Page, PageSize: $PageSize)"
                $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$CsoId/attributes/$encodedAttributeName/values?page=$Page&pageSize=$PageSize"
                if ($Search) {
                    $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                }

                $response = Invoke-JIMApi -Endpoint $endpoint
                foreach ($item in $response.items) {
                    $item
                }
            }

            'All' {
                Write-Verbose "Getting all attribute values for '$AttributeName' on connector space object $CsoId in Connected System $ConnectedSystemId"
                $pageRequest = {
                    param($p)
                    $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$CsoId/attributes/$encodedAttributeName/values?page=$p&pageSize=$PageSize"
                    if ($Search) {
                        $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                    }
                    Invoke-JIMApi -Endpoint $endpoint
                }

                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMConnectedSystemObjectAttributeValue' -PageSize $PageSize -Force:$Force `
                    -ItemNoun 'attribute values' -NarrowHint 'filter with -Search'
            }
        }
    }
}
