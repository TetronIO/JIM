function Get-JIMRole {
    <#
    .SYNOPSIS
        Gets security roles from JIM.

    .DESCRIPTION
        Retrieves security role definitions from JIM. Roles define permissions
        that can be assigned to users or API keys to control access to JIM
        functionality.

    .PARAMETER Name
        Filter roles by name. Supports wildcards (e.g., "Admin*").

    .OUTPUTS
        PSCustomObject representing role(s).

    .EXAMPLE
        Get-JIMRole

        Gets all security roles.

    .EXAMPLE
        Get-JIMRole -Name "Administrator"

        Gets the Administrator role.

    .EXAMPLE
        Get-JIMRole | Select-Object Id, Name, Description

        Gets all roles and displays specific properties.

    .LINK
        Get-JIMApiKey
        New-JIMApiKey
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter()]
        [SupportsWildcards()]
        [string]$Name
    )

    process {
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
