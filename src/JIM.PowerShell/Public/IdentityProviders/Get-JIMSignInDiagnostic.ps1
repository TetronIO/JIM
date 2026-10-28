# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSignInDiagnostic {
    <#
    .SYNOPSIS
        Shows the sign-in problems JIM has met, and the redirect URIs it sends the identity provider.

    .DESCRIPTION
        Lists the sign-in failures a setting explains that JIM has met since it started, the one seen most recently
        first: an ID token or access token for another audience or from another issuer, a client or scope the identity
        provider refused, or a sign-in carrying no claim of the type JIM identifies people by. Each names the setting
        involved, the value JIM expected, the value it received, and where to fix it. None holds a token, a secret or a
        claim's value. An access token is listed only when the identity provider signed it.

        Also shows the redirect URIs JIM sends the identity provider when reached at the address you connected to.
        Compare them with those the identity provider has registered for the portal: a redirect URI it does not have
        stops sign-in on its own error page, which JIM never sees.

        Requires the Administrator role. Before anyone can sign in, connect with the infrastructure API key.

    .OUTPUTS
        The sign-in problems, and the redirect URIs.

    .EXAMPLE
        Get-JIMSignInDiagnostic

        Shows the redirect URIs, and the problems JIM has met.

    .EXAMPLE
        (Get-JIMSignInDiagnostic).Problems | Format-Table Source, Setting, Expected, Received, Count

        Lists each problem with the setting involved and the values that did not match.

    .LINK
        Get-JIMIdentityProviderTrust
        Register-JIMIdentityProvider
    #>
    [CmdletBinding()]
    [OutputType('JIM.SignInDiagnosticsDto')]
    param()

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        try {
            Invoke-JIMApi -Endpoint '/api/v1/auth/sign-in-diagnostics'
        }
        catch {
            Write-Error "Failed to read the sign-in diagnostics: $_"
        }
    }
}
