function Disconnect-JIM {
    <#
    .SYNOPSIS
        Disconnects from the current JIM instance.

    .DESCRIPTION
        Clears the connection state and removes stored credentials from the session.
        After disconnecting, you must use Connect-JIM again before using other JIM cmdlets.

        For OAuth sessions, this clears the access token and refresh token from memory.
        Note: This does not sign you out of your identity provider.

    .OUTPUTS
        None.

    .EXAMPLE
        Disconnect-JIM

        Disconnects from the current JIM instance.

    .LINK
        Connect-JIM
        Test-JIMConnection
    #>
    [CmdletBinding()]
    param()

    if ($script:JIMConnection) {
        $authMethod = $script:JIMConnection.AuthMethod ?? 'Unknown'
        $url = $script:JIMConnection.Url

        Write-Verbose "Disconnecting from JIM at $url (auth method: $authMethod)"

        # Clear all connection data
        $script:JIMConnection = $null

        Write-Host "Disconnected from JIM at $url" -ForegroundColor Cyan

        if ($authMethod -eq 'OAuth') {
            Write-Verbose "OAuth tokens cleared from memory"
            Write-Host "Note: You are still signed into your identity provider. To fully sign out, close your browser or sign out from your identity provider." -ForegroundColor Gray
        }
    }
    else {
        Write-Verbose "No active JIM connection to disconnect"
    }
}
