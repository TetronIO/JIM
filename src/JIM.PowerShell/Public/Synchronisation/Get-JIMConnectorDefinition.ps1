# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectorDefinition {
    <#
    .SYNOPSIS
        Gets the Connectors JIM has.

    .DESCRIPTION
        Retrieves connector definition metadata including available settings, capabilities,
        and configuration options for each connector type.

    .PARAMETER Id
        The ID of the Connector to get.

    .PARAMETER Name
        The name of the Connector to get, matched exactly.

    .OUTPUTS
        The Connectors, or with -Id or -Name the one Connector, with its settings and what it supports.

    .EXAMPLE
        Get-JIMConnectorDefinition

        Gets all available connector definitions.

    .EXAMPLE
        Get-JIMConnectorDefinition -Name "CSV File"

        Gets the CSV File connector definition.

    .EXAMPLE
        Get-JIMConnectorDefinition -Id 2

        Gets a specific connector definition by ID.

    .LINK
        New-JIMConnectedSystem
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.ConnectorDefinitionHeader', ParameterSetName = 'List')]
    [OutputType('JIM.ConnectorDefinitionDto', ParameterSetName = 'ById')]
    [OutputType('JIM.ConnectorDefinitionDto', ParameterSetName = 'ByName')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting connector definition with ID: $Id"
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connector-definitions/$Id"
            }

            'ByName' {
                Write-Verbose "Getting connector definition with name: $Name"
                $encodedName = [System.Uri]::EscapeDataString($Name)
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connector-definitions/by-name/$encodedName"
            }

            'List' {
                Write-Verbose "Getting all connector definitions"
                $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connector-definitions"

                # Output each definition individually for pipeline support
                foreach ($def in $response) {
                    $def
                }
            }
        }
    }
}
