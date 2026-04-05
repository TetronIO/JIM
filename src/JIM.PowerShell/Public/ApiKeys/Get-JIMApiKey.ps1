function Get-JIMApiKey {
    <#
    .SYNOPSIS
        Gets API Keys from JIM.

    .DESCRIPTION
        Retrieves API Key information from JIM. Can retrieve all keys or a specific
        key by ID. Note that the full key value is never returned; only the key
        prefix is shown for identification.

    .PARAMETER Id
        The unique identifier (GUID) of a specific API Key to retrieve.

    .PARAMETER Name
        Filter API Keys by name. Supports wildcards (e.g., "CI*").

    .OUTPUTS
        PSCustomObject representing API Key(s).

    .EXAMPLE
        Get-JIMApiKey

        Gets all API Keys.

    .EXAMPLE
        Get-JIMApiKey -Id "12345678-1234-1234-1234-123456789abc"

        Gets the API Key with the specified ID.

    .EXAMPLE
        Get-JIMApiKey -Name "CI*"

        Gets all API Keys with names starting with "CI".

    .LINK
        New-JIMApiKey
        Set-JIMApiKey
        Remove-JIMApiKey
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [SupportsWildcards()]
        [string]$Name
    )

    process {
        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting API Key with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/apikeys/$Id"
                $result
            }

            'List' {
                Write-Verbose "Getting all API Keys"
                $response = Invoke-JIMApi -Endpoint "/api/v1/apikeys"

                # Filter by name if specified
                if ($Name) {
                    Write-Verbose "Filtering by name pattern: $Name"
                    $response = $response | Where-Object { $_.name -like $Name }
                }

                # Output each key individually for pipeline support
                foreach ($key in $response) {
                    $key
                }
            }
        }
    }
}
