# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Disconnect-JIM {
    <#
    .SYNOPSIS
        Disconnects from a JIM instance and forgets its persisted credentials.

    .DESCRIPTION
        Clears the in-memory session and removes the persisted refresh token for the
        instance from the operating system credential store, so a later Connect-JIM
        must authenticate again.

        By default this targets the currently connected instance. Use -Url to target a
        specific instance (which may or may not be the connected one, and need not be
        connected at all), or -All to remove every persisted JIM credential on the
        machine; the latter is useful when several JIM instances are in play.

        Removal is auth-agnostic: a stored refresh token for the targeted instance is
        removed even if the cleared session authenticated with an API key. (API keys
        themselves are never persisted by the module; the caller supplies them on each
        Connect-JIM.)

        For OAuth sessions this clears the access and refresh tokens from memory. Note
        that it does not sign you out of your identity provider.

    .PARAMETER Url
        The JIM instance to disconnect and forget. Defaults to the currently connected
        instance. An explicit -Url works even when there is no active connection, since
        clearing the credential store is a maintenance operation independent of session
        state.

    .PARAMETER All
        Remove every persisted JIM refresh token from the credential store, across all
        instances, and clear any in-memory session.

    .OUTPUTS
        None.

    .EXAMPLE
        Disconnect-JIM

        Disconnects the current session and removes the persisted refresh token for the
        connected instance.

    .EXAMPLE
        Disconnect-JIM -Url "https://jim.company.com"

        Removes the persisted refresh token for a specific instance, even if not
        currently connected to it.

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

    if ($PSBoundParameters.ContainsKey('Url') -and -not ($Url -match '^https?://')) {
        throw "Invalid URL format. URL must start with http:// or https://"
    }

    # Determine which instance to forget: an explicit -Url wins, otherwise the
    # currently connected instance. Null means there is nothing to target.
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
            Write-Host "Note: You are still signed into your identity provider. To fully sign out, close your browser or sign out from your identity provider." -ForegroundColor Gray
        }
    }
    else {
        Write-Verbose "No active JIM connection to disconnect"
    }

    # Remove the persisted refresh token for the targeted instance. Auth-agnostic: a
    # stored token is removed even if the cleared session used an API key. A bare
    # Disconnect-JIM while not connected has no target, so it is a quiet no-op.
    if ($targetUrl) {
        try {
            $removed = Remove-JIMToken -BaseUrl $targetUrl
            if ($removed -gt 0) {
                Write-Host "Removed persisted credentials for $targetUrl from the OS credential store." -ForegroundColor Cyan
            }
            else {
                Write-Verbose "No persisted credentials found for $targetUrl."
            }
        }
        catch {
            Write-Warning "Failed to remove persisted credentials for $targetUrl`: $_"
        }
    }
    else {
        Write-Verbose "No active connection and no -Url specified; nothing to remove from the credential store. Use -Url or -All."
    }
}
