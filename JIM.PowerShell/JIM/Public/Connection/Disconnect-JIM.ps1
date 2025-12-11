function Disconnect-JIM {
    <#
    .SYNOPSIS
        Disconnects from the current JIM instance.

    .DESCRIPTION
        Clears the connection state and removes stored credentials from the session.
        After disconnecting, you must use Connect-JIM again before using other JIM cmdlets.

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
        Write-Verbose "Disconnecting from JIM at $($script:JIMConnection.Url)"
        $script:JIMConnection = $null
        Write-Verbose "Disconnected successfully"
    }
    else {
        Write-Verbose "No active JIM connection to disconnect"
    }
}
