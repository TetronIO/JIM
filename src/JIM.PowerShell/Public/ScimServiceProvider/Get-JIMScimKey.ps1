# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMScimKey {
    <#
    .SYNOPSIS
        Gets the keys an identity provider calls a Connected System's SCIM endpoint with.

    .DESCRIPTION
        Retrieves the keys scoped to the SCIM 2.0 Service Provider endpoint of one Connected System, usable or not,
        with each one's expiry and last use, so you can tell when an old key has gone quiet after a rotation. The
        secret is never returned; KeyPrefix identifies a key.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector.

    .PARAMETER Id
        The ID of one key to return.

    .PARAMETER Name
        Returns only the keys whose name matches, such as "Entra*".

    .OUTPUTS
        Each key, or the one key asked for.

    .EXAMPLE
        Get-JIMScimKey -ConnectedSystemId 12

        Lists the keys of Connected System 12's SCIM endpoint.

    .EXAMPLE
        Get-JIMScimKey -ConnectedSystemId 12 | Select-Object Name, IsEnabled, ExpiresAt, LastUsedAt

        Shows when each key expires and when it was last used.

    .LINK
        New-JIMScimKey
        Set-JIMScimKey
        Remove-JIMScimKey
        Get-JIMScimStoreSummary
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.ApiKeyDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ScopeConnectedSystemId')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [SupportsWildcards()]
        [string]$Name
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scim/keys"
        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting SCIM key $Id of Connected System $ConnectedSystemId"
                Invoke-JIMApi -Endpoint "$endpoint/$Id"
            }

            'List' {
                Write-Verbose "Getting the SCIM keys of Connected System $ConnectedSystemId"
                $keys = Invoke-JIMApi -Endpoint $endpoint
                if ($Name) {
                    $keys = $keys | Where-Object { $_.name -like $Name }
                }
                foreach ($key in $keys) {
                    $key
                }
            }
        }
    }
}
