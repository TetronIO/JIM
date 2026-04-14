# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMUserInfo {
    <#
    .SYNOPSIS
        Retrieves the current authenticated user's details, roles, and authorisation status.

    .DESCRIPTION
        Calls the JIM userinfo endpoint to retrieve information about the currently
        authenticated user, including their display name, authentication method,
        roles, and whether they are authorised to use JIM.

        This cmdlet requires an active Connect-JIM session.

    .OUTPUTS
        PSCustomObject with user details including authorised, isAdministrator,
        name, authMethod, metaverseObjectId, roles, and message properties.

    .EXAMPLE
        Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"
        Get-JIMUserInfo

    .EXAMPLE
        $user = Get-JIMUserInfo
        if ($user.isAdministrator) { Write-Host "Admin access confirmed" }
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param()

    process {
        Write-Verbose "Getting current user info"

        $response = Invoke-JIMApi -Endpoint '/api/v1/userinfo'
        $response
    }
}
