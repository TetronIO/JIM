# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMScimResourceType {
    <#
    .SYNOPSIS
        Gets the SCIM resource types a Connected System's endpoint serves.

    .DESCRIPTION
        Returns the RFC 7643 resource type documents a Connected System's SCIM 2.0 Service Provider endpoint
        publishes, each naming its endpoint, base schema and schema extensions. They are read-only.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector.

    .PARAMETER Name
        Returns only the resource types whose name matches, such as "User".

    .OUTPUTS
        Each resource type.

    .EXAMPLE
        Get-JIMScimResourceType -ConnectedSystemId 12 | Select-Object Name, Endpoint, Schema

        Lists the resource types with their endpoints and base schemas.

    .LINK
        Get-JIMScimSchema
        Get-JIMScimStoreSummary
    #>
    [CmdletBinding()]
    [OutputType('JIM.ScimResourceType')]
    param(
        # Aliased so a Connected System piped in binds by its Id; a resource type is identified by its name, not an ID.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [SupportsWildcards()]
        [string]$Name
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting the SCIM resource types of Connected System $ConnectedSystemId"
        $resourceTypes = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scim/resource-types"
        if ($Name) {
            $resourceTypes = $resourceTypes | Where-Object { $_.name -like $Name }
        }
        foreach ($resourceType in $resourceTypes) {
            $resourceType
        }
    }
}
