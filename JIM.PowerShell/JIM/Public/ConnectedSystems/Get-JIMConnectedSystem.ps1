function Get-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Gets Connected Systems from JIM.

    .DESCRIPTION
        Retrieves Connected System configurations from JIM. Can retrieve all systems,
        a specific system by ID, or filter by name using wildcards.

    .PARAMETER Id
        The unique identifier of a specific Connected System to retrieve.

    .PARAMETER Name
        Filter Connected Systems by name. Supports wildcards (e.g., "HR*").

    .PARAMETER ObjectTypes
        If specified, retrieves the object types defined in the Connected System's schema.
        Only valid when -Id is specified.

    .PARAMETER ObjectId
        Retrieves a specific object from the Connected System by its GUID.
        Only valid when -Id is specified.

    .PARAMETER DeletionPreview
        Gets a preview of what would be affected by deleting the Connected System.
        Only valid when -Id is specified.

    .OUTPUTS
        PSCustomObject representing Connected System(s), object types, or deletion preview.

    .EXAMPLE
        Get-JIMConnectedSystem

        Gets all Connected Systems.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1

        Gets the Connected System with ID 1.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*"

        Gets all Connected Systems with names starting with "HR".

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 -ObjectTypes

        Gets the object types defined in the Connected System's schema.

    .EXAMPLE
        Get-JIMConnectedSystem -Id 1 -DeletionPreview

        Gets a preview of what would be affected by deleting the Connected System.

    .LINK
        New-JIMConnectedSystem
        Set-JIMConnectedSystem
        Remove-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ObjectTypes', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Object', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'DeletionPreview', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(ParameterSetName = 'List')]
        [SupportsWildcards()]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'ObjectTypes')]
        [switch]$ObjectTypes,

        [Parameter(Mandatory, ParameterSetName = 'Object')]
        [guid]$ObjectId,

        [Parameter(Mandatory, ParameterSetName = 'DeletionPreview')]
        [switch]$DeletionPreview
    )

    process {
        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Connected System with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$Id"
                $result
            }

            'ObjectTypes' {
                Write-Verbose "Getting object types for Connected System ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$Id/object-types"
                $result
            }

            'Object' {
                Write-Verbose "Getting object $ObjectId from Connected System ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$Id/objects/$ObjectId"
                $result
            }

            'DeletionPreview' {
                Write-Verbose "Getting deletion preview for Connected System ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$Id/deletion-preview"
                $result
            }

            'List' {
                Write-Verbose "Getting all Connected Systems"
                $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems"

                # Handle paginated response
                $systems = if ($response.items) { $response.items } else { $response }

                # Filter by name if specified
                if ($Name) {
                    Write-Verbose "Filtering by name pattern: $Name"
                    $systems = $systems | Where-Object { $_.name -like $Name }
                }

                # Output each system individually for pipeline support
                foreach ($system in $systems) {
                    $system
                }
            }
        }
    }
}
