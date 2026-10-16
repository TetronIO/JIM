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
        One object saying who the caller is to JIM. A caller with no JIM identity is answered rather than refused:
        Authorised is $false and Message says what to do.

    .EXAMPLE
        Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_ak_xxxxxxxxxxxx"
        Get-JIMUserInfo

    .EXAMPLE
        $user = Get-JIMUserInfo
        if ($user.isAdministrator) { Write-Host "Admin access confirmed" }
    #>
    [CmdletBinding()]
    [OutputType('JIM.UserInfoResponse')]
    param()

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting current user info"

        $response = Invoke-JIMApi -Endpoint '/api/v1/userinfo'
        $response
    }
}
