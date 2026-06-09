# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMRole {
    <#
    .SYNOPSIS
        Gets security roles from JIM.

    .DESCRIPTION
        Retrieves security role definitions from JIM. Roles define permissions
        that can be assigned to users or API keys to control access to JIM
        functionality.

        Use -Id to retrieve a specific role by its numeric identifier, or
        -Name to filter roles by name (supports wildcards). When called
        without parameters, all roles are returned.

    .PARAMETER Id
        The unique identifier (integer) of the role to retrieve.

    .PARAMETER Name
        Filter roles by name. Supports wildcards (e.g., "Admin*").

    .OUTPUTS
        PSCustomObject representing role(s).

    .EXAMPLE
        Get-JIMRole

        Gets all security roles.

    .EXAMPLE
        Get-JIMRole -Id 1

        Gets the role with ID 1.

    .EXAMPLE
        Get-JIMRole -Name "Administrator"

        Gets the Administrator role.

    .EXAMPLE
        Get-JIMRole -Name "Admin*"

        Gets all roles matching the wildcard pattern.

    .EXAMPLE
        Get-JIMRole | Select-Object Id, Name

        Gets all roles and displays specific properties.

    .LINK
        Get-JIMRoleMember
        Get-JIMApiKey
        New-JIMApiKey
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(ParameterSetName = 'List')]
        [SupportsWildcards()]
        [string]$Name
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            Write-Verbose "Getting role by ID: $Id"
            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/security/roles/$Id"
                $response
            }
            catch {
                Write-Error "Failed to get role: $_"
            }
            return
        }

        Write-Verbose "Getting security roles"

        $response = Invoke-JIMApi -Endpoint "/api/v1/security/roles"

        # Filter by name if specified
        if ($Name) {
            Write-Verbose "Filtering by name pattern: $Name"
            $response = $response | Where-Object { $_.name -like $Name }
        }

        # Output each role individually for pipeline support
        foreach ($role in $response) {
            $role
        }
    }
}
