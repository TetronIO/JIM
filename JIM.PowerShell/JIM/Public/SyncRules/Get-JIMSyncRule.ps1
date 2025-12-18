function Get-JIMSyncRule {
    <#
    .SYNOPSIS
        Gets Synchronisation Rules from JIM.

    .DESCRIPTION
        Retrieves Synchronisation Rule configurations from JIM. Can retrieve all rules,
        a specific rule by ID, or filter by Connected System.

    .PARAMETER Id
        The unique identifier of a specific Sync Rule to retrieve.

    .PARAMETER ConnectedSystemId
        Filter Sync Rules by Connected System ID.

    .PARAMETER ConnectedSystemName
        Filter Sync Rules by Connected System name. Must be an exact match.

    .OUTPUTS
        PSCustomObject representing Sync Rule(s).

    .EXAMPLE
        Get-JIMSyncRule

        Gets all Synchronisation Rules.

    .EXAMPLE
        Get-JIMSyncRule -Id 1

        Gets the Sync Rule with ID 1.

    .EXAMPLE
        Get-JIMSyncRule -ConnectedSystemId 1

        Gets all Sync Rules for Connected System ID 1.

    .EXAMPLE
        Get-JIMSyncRule -ConnectedSystemName 'Contoso AD'

        Gets all Sync Rules for the Connected System named 'Contoso AD'.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*" | Get-JIMSyncRule

        Gets all Sync Rules for Connected Systems with names starting with "HR".

    .LINK
        New-JIMSyncRule
        Set-JIMSyncRule
        Remove-JIMSyncRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$Id,

        [Parameter(ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(ParameterSetName = 'ByConnectedSystemId')]
        [int]$ConnectedSystemId,

        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [string]$ConnectedSystemName
    )

    process {
        # Resolve ConnectedSystemName to ConnectedSystemId if specified
        if ($PSBoundParameters.ContainsKey('ConnectedSystemName')) {
            $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
            $ConnectedSystemId = $connectedSystem.id
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Sync Rule with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$Id"
                $result
            }

            default {
                Write-Verbose "Getting all Sync Rules"
                $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules"

                # Handle paginated response - check if 'items' property exists (not if it's truthy)
                $rules = if ($null -ne $response.items) { $response.items } else { $response }

                # Filter by Connected System if specified
                if ($PSBoundParameters.ContainsKey('ConnectedSystemId') -or $PSBoundParameters.ContainsKey('ConnectedSystemName')) {
                    Write-Verbose "Filtering by Connected System ID: $ConnectedSystemId"
                    $rules = $rules | Where-Object { $_.connectedSystemId -eq $ConnectedSystemId }
                }

                # Output each rule individually for pipeline support
                foreach ($rule in $rules) {
                    $rule
                }
            }
        }
    }
}
