function Get-JIMConnectorDefinition {
    <#
    .SYNOPSIS
        Gets connector definitions from JIM.

    .DESCRIPTION
        Retrieves connector definition metadata including available settings, capabilities,
        and configuration options for each connector type.

    .PARAMETER Id
        Optional ID of a specific connector definition to retrieve.

    .OUTPUTS
        Array of connector definition objects, or a single connector definition if Id is specified.

    .EXAMPLE
        Get-JIMConnectorDefinition

        Gets all available connector definitions.

    .EXAMPLE
        $csvConnector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "CSV File" }

        Gets the CSV File connector definition.

    .EXAMPLE
        Get-JIMConnectorDefinition -Id 2

        Gets a specific connector definition by ID.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false, ValueFromPipeline = $true, ValueFromPipelineByPropertyName = $true)]
        [int]$Id
    )

    process {
        if ($Id) {
            # Get specific connector definition
            $endpoint = "/api/v1/synchronisation/connector-definitions/$Id"
            Invoke-JIMApi -Endpoint $endpoint
        }
        else {
            # Get all connector definitions
            $endpoint = "/api/v1/synchronisation/connector-definitions"
            Invoke-JIMApi -Endpoint $endpoint
        }
    }
}
