function Get-JIMConnectorDefinition {
    <#
    .SYNOPSIS
        Gets connector definitions from JIM.

    .DESCRIPTION
        Retrieves connector definition metadata including available settings, capabilities,
        and configuration options for each connector type.

    .PARAMETER Id
        The unique identifier of a specific connector definition to retrieve.

    .PARAMETER Name
        The name of a specific connector definition to retrieve. Must be an exact match.

    .OUTPUTS
        Array of connector definition objects, or a single connector definition if Id or Name is specified.

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
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name
    )

    process {
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
