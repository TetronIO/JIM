# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMCredentialHeader {
    <#
    .SYNOPSIS
        Builds the Authorization header for the current JIM connection.

    .DESCRIPTION
        Every credential JIM accepts is a bearer token in the Authorization header (RFC 6750): an API key
        (jim_ak_...) and an SSO access token alike. Every request that carries a credential builds its header
        here, so no call site can send a key any other way.

        The returned hashtable holds the credential. Never write it to any output stream.

    .OUTPUTS
        Hashtable with a single Authorization entry.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param()

    $token = switch ($script:JIMConnection.AuthMethod) {
        'ApiKey' { $script:JIMConnection.ApiKey }
        'OAuth' { $script:JIMConnection.AccessToken }
        default { throw "Unknown authentication method: $($script:JIMConnection.AuthMethod)" }
    }

    return @{ 'Authorization' = "Bearer $token" }
}
