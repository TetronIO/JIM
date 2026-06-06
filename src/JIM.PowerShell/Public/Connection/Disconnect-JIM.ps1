# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Disconnect-JIM {
    <#
    .SYNOPSIS
        Disconnects from the current JIM instance.

    .DESCRIPTION
        Clears the connection state and removes stored credentials from the session.
        After disconnecting, you must use Connect-JIM again before using other JIM cmdlets.

        For OAuth sessions, this clears the access token and refresh token from memory.
        Note: This does not sign you out of your identity provider.

        By default the persisted refresh token in the operating system credential store
        is left intact, so a later Connect-JIM can still reconnect silently. Use
        -ClearCache to also remove the persisted refresh token. -ClearCache works even
        when there is no active connection, since clearing the credential store is a
        maintenance operation independent of session state.

    .PARAMETER ClearCache
        Also remove the persisted refresh token from the OS credential store. Without
        -Url or -All, this targets the currently connected instance.

    .PARAMETER Url
        The JIM instance whose persisted refresh token should be removed from the
        credential store. Implies -ClearCache. Useful when not currently connected.

    .PARAMETER All
        Remove every persisted JIM refresh token from the credential store.

    .OUTPUTS
        None.

    .EXAMPLE
        Disconnect-JIM

        Disconnects the current session. Any persisted refresh token is left in place.

    .EXAMPLE
        Disconnect-JIM -ClearCache

        Disconnects and removes the persisted refresh token for the current instance.

    .EXAMPLE
        Disconnect-JIM -Url "https://jim.company.com" -ClearCache

        Removes the persisted refresh token for a specific instance, even if not connected.

    .EXAMPLE
        Disconnect-JIM -All

        Disconnects and removes every persisted JIM refresh token from this machine.

    .LINK
        Connect-JIM
        Test-JIMConnection
    #>
    [CmdletBinding(DefaultParameterSetName = 'Default')]
    param(
        [Parameter(ParameterSetName = 'Default')]
        [ValidateNotNullOrEmpty()]
        [string]$Url,

        [Parameter(ParameterSetName = 'Default')]
        [switch]$ClearCache,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All
    )

    # -All: clear every persisted token, plus any in-memory session.
    if ($PSCmdlet.ParameterSetName -eq 'All') {
        $script:JIMConnection = $null
        try {
            $removed = Remove-JIMToken -All
            Write-Host "Removed $removed persisted JIM credential(s) from the OS credential store." -ForegroundColor Cyan
        }
        catch {
            Write-Warning "Failed to clear persisted JIM credentials: $_"
        }
        return
    }

    # A -Url targets the credential store and so implies -ClearCache.
    $shouldClearCache = $ClearCache -or $PSBoundParameters.ContainsKey('Url')

    if ($PSBoundParameters.ContainsKey('Url') -and -not ($Url -match '^https?://')) {
        throw "Invalid URL format. URL must start with http:// or https://"
    }

    # Determine which instance's persisted token to remove: an explicit -Url wins,
    # otherwise the currently connected instance.
    $targetUrl = if ($PSBoundParameters.ContainsKey('Url')) {
        $Url
    }
    elseif ($script:JIMConnection) {
        $script:JIMConnection.Url
    }
    else {
        $null
    }

    # Clear the in-memory session.
    if ($script:JIMConnection) {
        $authMethod = $script:JIMConnection.AuthMethod ?? 'Unknown'
        $sessionUrl = $script:JIMConnection.Url

        Write-Verbose "Disconnecting from JIM at $sessionUrl (auth method: $authMethod)"
        $script:JIMConnection = $null
        Write-Host "Disconnected from JIM at $sessionUrl" -ForegroundColor Cyan

        if ($authMethod -eq 'OAuth') {
            Write-Verbose "OAuth tokens cleared from memory"
            Write-Host "Note: You are still signed into your identity provider. To fully sign out, close your browser or sign out from your identity provider." -ForegroundColor Gray
        }
    }
    else {
        Write-Verbose "No active JIM connection to disconnect"
    }

    # Optionally remove the persisted refresh token.
    if ($shouldClearCache) {
        if ($targetUrl) {
            try {
                $removed = Remove-JIMToken -BaseUrl $targetUrl
                if ($removed -gt 0) {
                    Write-Host "Removed persisted credentials for $targetUrl from the OS credential store." -ForegroundColor Cyan
                }
                else {
                    Write-Host "No persisted credentials found for $targetUrl." -ForegroundColor Gray
                }
            }
            catch {
                Write-Warning "Failed to remove persisted credentials for $targetUrl`: $_"
            }
        }
        else {
            Write-Warning "No URL specified and no active connection; nothing to clear from the credential store. Use -Url or -All."
        }
    }
}
