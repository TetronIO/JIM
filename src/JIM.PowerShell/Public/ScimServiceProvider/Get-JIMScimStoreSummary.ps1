# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMScimStoreSummary {
    <#
    .SYNOPSIS
        Gets a Connected System's SCIM endpoint and what its store holds.

    .DESCRIPTION
        Returns the base URL to give an identity provider, how many resources of each type the Connected System's
        SCIM store holds, deleted resources kept until an import reads them, how many changes the next import will
        read, and when a SCIM client last wrote.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector.

    .OUTPUTS
        The summary.

    .EXAMPLE
        Get-JIMScimStoreSummary -ConnectedSystemId 12

        Shows Connected System 12's SCIM base URL and store.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Entra provisioning" | Get-JIMScimStoreSummary | Select-Object BaseUrl, ChangesAwaitingImport

        Shows the base URL and the import backlog of the Connected System with that name.

    .LINK
        Get-JIMScimKey
        Get-JIMScimResourceType
        Get-JIMScimSchema
    #>
    [CmdletBinding()]
    [OutputType('JIM.ScimServiceProviderSummaryDto')]
    param(
        # Aliased so a Connected System piped in binds by its Id; the summary has no ID of its own to confuse it with.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting the SCIM store summary of Connected System $ConnectedSystemId"
        Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scim/summary"
    }
}
