# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMScimSchema {
    <#
    .SYNOPSIS
        Gets the SCIM schemas a Connected System's endpoint serves.

    .DESCRIPTION
        Returns the RFC 7643 schema documents a Connected System's SCIM 2.0 Service Provider endpoint publishes, with
        every attribute's characteristics: its type, whether it is multi-valued or required, its mutability and
        whether it is returned. They are read-only.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector.

    .OUTPUTS
        Each schema.

    .EXAMPLE
        (Get-JIMScimSchema -ConnectedSystemId 12 | Where-Object { $_.Name -eq 'User' }).Attributes | Select-Object Name, Type, Required

        Lists the attributes of the core User schema.

    .LINK
        Get-JIMScimResourceType
        Get-JIMScimStoreSummary
    #>
    [CmdletBinding()]
    [OutputType('JIM.ScimSchema')]
    param(
        # Aliased so a Connected System piped in binds by its Id; a schema is identified by its URN, not an ID.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting the SCIM schemas of Connected System $ConnectedSystemId"
        $schemas = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scim/schemas"
        foreach ($schema in $schemas) {
            $schema
        }
    }
}
