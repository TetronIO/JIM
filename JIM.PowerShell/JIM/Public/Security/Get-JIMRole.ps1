function Get-JIMRole {
    <#
    .SYNOPSIS
        Gets security roles from JIM.

    .DESCRIPTION
        Retrieves security role definitions from JIM. Roles define permissions
        that can be assigned to users or API keys to control access to JIM
        functionality.

    .OUTPUTS
        PSCustomObject representing role(s).

    .EXAMPLE
        Get-JIMRole

        Gets all security roles.

    .EXAMPLE
        Get-JIMRole | Select-Object Id, Name, Description

        Gets all roles and displays specific properties.

    .LINK
        Get-JIMApiKey
        New-JIMApiKey
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param()

    process {
        Write-Verbose "Getting security roles"

        $response = Invoke-JIMApi -Endpoint "/api/v1/security/roles"

        # Output each role individually for pipeline support
        foreach ($role in $response) {
            $role
        }
    }
}
