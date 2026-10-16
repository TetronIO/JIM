# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Update-JIMExpiringAccessToken {
    <#
    .SYNOPSIS
        Refreshes an SSO connection's access token when it has expired or will within two minutes.

    .DESCRIPTION
        Called before every request that carries the connection's credential, so a long session keeps working
        without the caller noticing. An API key connection has nothing to refresh. When the token cannot be
        refreshed, Invoke-TokenRefresh throws, telling the caller to run Connect-JIM again; the request is not sent.
    #>
    [CmdletBinding()]
    param()

    if ($script:JIMConnection.AuthMethod -eq 'OAuth' -and $script:JIMConnection.TokenExpiresAt -and
        $script:JIMConnection.TokenExpiresAt -lt (Get-Date).AddMinutes(2)) {
        Invoke-TokenRefresh -Reason "Access token expired or expiring soon"
    }
}
