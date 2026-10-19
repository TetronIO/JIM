# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMIdentityProviderTrust {
    <#
    .SYNOPSIS
        Shows whether JIM trusts its identity provider's TLS certificate.

    .DESCRIPTION
        Reads whether JIM can sign people in through its identity provider, as far as the identity provider's
        certificate decides it, and the certificate chain the identity provider presents. JIM checks again at most
        every few seconds however often this is called.

        When the state is Untrusted, trust the certificate named by suggestedThumbprint with
        Add-JIMCertificate -FromIdentityProvider. Before anyone can sign in, connect with the infrastructure API key
        to do so. A NameMismatch cannot be fixed by trusting a certificate: the identity provider needs a certificate
        that names its host.

        Administrators also see what the JIM certificate store holds for each certificate in the chain.

    .OUTPUTS
        The identity provider's trust status.

    .EXAMPLE
        Get-JIMIdentityProviderTrust

        Shows the state, the host JIM connects to, and why.

    .EXAMPLE
        (Get-JIMIdentityProviderTrust).presentedChain | Format-Table commonName, thumbprint, validTo

        Lists the certificates the identity provider presents, its own first.

    .LINK
        Add-JIMCertificate
        Get-JIMCertificate
    #>
    [CmdletBinding()]
    [OutputType('JIM.IdentityProviderTrustStatusDto')]
    param()

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        try {
            Invoke-JIMApi -Endpoint '/api/v1/auth/identity-provider-trust'
        }
        catch {
            Write-Error "Failed to read the identity provider's trust status: $_"
        }
    }
}
