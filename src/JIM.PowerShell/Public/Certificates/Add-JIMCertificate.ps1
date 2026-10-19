# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Add-JIMCertificate {
    <#
    .SYNOPSIS
        Adds a certificate to JIM's trusted certificate store.

    .DESCRIPTION
        Adds a certificate from a file path, from raw certificate data, or straight from the chain JIM's identity
        provider presents. Both PEM and DER formats are supported.

        A certificate is trusted for Connectors (the servers Connected Systems connect to), for sign-in (the
        identity provider), or both. Each purpose only ever uses the certificates trusted for it.

        -FromIdentityProvider trusts a certificate the identity provider presents, for sign-in. JIM connects to the
        identity provider again and trusts only a certificate it is presenting now. Name it with -Thumbprint, having
        checked the thumbprint against the one your certificate authority publishes. Without -Thumbprint, the cmdlet
        shows the certificate JIM suggests (the highest one presented) and asks before trusting it. Sign-in works
        straight away; nothing needs restarting. Before anyone can sign in, connect with the infrastructure API key.

    .PARAMETER Name
        The name the certificate is shown by in the store. Optional with -FromIdentityProvider, where the
        certificate's common name is used.

    .PARAMETER Path
        Path to a certificate file (PEM or DER format). The path should be
        accessible from the JIM server (e.g., in the connector-files volume).

    .PARAMETER CertificateData
        Raw certificate data as a byte array.

    .PARAMETER CertificateBase64
        Certificate data as a Base64-encoded string.

    .PARAMETER FromIdentityProvider
        Trusts a certificate JIM's identity provider presents now, for sign-in.

    .PARAMETER Thumbprint
        With -FromIdentityProvider, the thumbprint of the certificate to trust, as Get-JIMIdentityProviderTrust shows
        it. Spaces and colons are ignored. Without it, the cmdlet suggests one and asks first.

    .PARAMETER TrustedFor
        What the certificate is trusted for: Connectors, SignIn, or both. Connectors when not given.

    .PARAMETER Notes
        Optional notes about the certificate.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the certificate's
        Configuration Change History.

    .PARAMETER Force
        With -FromIdentityProvider and no -Thumbprint, trusts the suggested certificate without asking.

    .PARAMETER PassThru
        Returns the certificate added, or with -FromIdentityProvider the outcome.

    .OUTPUTS
        With -PassThru, the certificate added, or with -FromIdentityProvider the outcome, the certificate as it now
        sits in the store, and the identity provider's status afterwards; otherwise nothing.

    .EXAMPLE
        Add-JIMCertificate -Name "LDAP CA" -Path "/connector-files/ldap-ca.pem" -PassThru

        Adds a certificate from a file path.

    .EXAMPLE
        $certData = [System.IO.File]::ReadAllBytes("./cert.der")
        Add-JIMCertificate -Name "My Cert" -CertificateData $certData -PassThru

        Adds a certificate from raw byte data.

    .EXAMPLE
        $base64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes("./cert.der"))
        Add-JIMCertificate -Name "My Cert" -CertificateBase64 $base64 -PassThru

        Adds a certificate from Base64-encoded data.

    .EXAMPLE
        Connect-JIM -Url https://jim.corp.contoso.com -ApiKey $env:JIM_INFRASTRUCTURE_API_KEY
        Add-JIMCertificate -FromIdentityProvider -Thumbprint '8F3A91D06B2E47C2C21D9F0E1A2B3C4D5E6F7A8B' -PassThru

        Trusts the identity provider's issuing certificate authority for sign-in, before anyone can sign in. Check the
        thumbprint against the one your certificate authority publishes first.

    .EXAMPLE
        Add-JIMCertificate -FromIdentityProvider

        Shows the certificate JIM suggests trusting, and asks before trusting it for sign-in.

    .EXAMPLE
        $base64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes("./contoso-root.cer"))
        Add-JIMCertificate -Name "Contoso Root CA" -CertificateBase64 $base64 -TrustedFor SignIn, Connectors

        Trusts an internal root for sign-in and for Connectors alike. A root outlasts every certificate it issues.

    .LINK
        Get-JIMIdentityProviderTrust
        Get-JIMCertificate
        Set-JIMCertificate
        Remove-JIMCertificate
        Test-JIMCertificate
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'FromFile')]
    [OutputType('JIM.TrustedCertificateDetailDto', ParameterSetName = ('FromFile', 'FromData', 'FromBase64'))]
    [OutputType('JIM.TrustIdentityProviderCertificateResponse', ParameterSetName = 'FromIdentityProvider')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'FromFile')]
        [Parameter(Mandatory, ParameterSetName = 'FromData')]
        [Parameter(Mandatory, ParameterSetName = 'FromBase64')]
        [Parameter(ParameterSetName = 'FromIdentityProvider')]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'FromFile')]
        [string]$Path,

        [Parameter(Mandatory, ParameterSetName = 'FromData')]
        [byte[]]$CertificateData,

        [Parameter(Mandatory, ParameterSetName = 'FromBase64')]
        [string]$CertificateBase64,

        [Parameter(Mandatory, ParameterSetName = 'FromIdentityProvider')]
        [switch]$FromIdentityProvider,

        [Parameter(ParameterSetName = 'FromIdentityProvider')]
        [ValidateNotNullOrEmpty()]
        [string]$Thumbprint,

        [Parameter(ParameterSetName = 'FromFile')]
        [Parameter(ParameterSetName = 'FromData')]
        [Parameter(ParameterSetName = 'FromBase64')]
        [ValidateSet('Connectors', 'SignIn')]
        [string[]]$TrustedFor,

        [Parameter(ParameterSetName = 'FromFile')]
        [Parameter(ParameterSetName = 'FromData')]
        [Parameter(ParameterSetName = 'FromBase64')]
        [string]$Notes,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [Parameter(ParameterSetName = 'FromIdentityProvider')]
        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'FromIdentityProvider') {
            $chosen = $Thumbprint
            $description = $Thumbprint

            if (-not $chosen) {
                try {
                    $status = Invoke-JIMApi -Endpoint '/api/v1/auth/identity-provider-trust'
                }
                catch {
                    Write-Error "Failed to read the identity provider's trust status: $_"
                    return
                }

                $suggested = @($status.presentedChain) | Where-Object { $_.thumbprint -eq $status.suggestedThumbprint } | Select-Object -First 1
                if ($status.state -ne 'Untrusted' -or -not $suggested) {
                    $reason = if ($status.state -eq 'Trusted') { "JIM already trusts the identity provider." } else { $status.message }
                    Write-Error "There is no certificate to trust from the identity provider. $reason"
                    return
                }

                $chosen = $suggested.thumbprint
                $description = "'$($suggested.commonName)' ($chosen)"

                if (-not $PSCmdlet.ShouldProcess($description, "Trust for sign-in")) {
                    return
                }

                if (-not $Force) {
                    $role = if ($suggested.isServerCertificate) { "the identity provider's own certificate" } else { 'certificate authority' }
                    $validTo = ([datetime]$suggested.validTo).ToString('dd MMM yyyy', [System.Globalization.CultureInfo]::InvariantCulture)
                    $query = @(
                        "Identity provider: $($status.host)"
                        "Certificate:       $($suggested.commonName) ($role)"
                        "Thumbprint:        $chosen"
                        "Valid until:       $validTo"
                        ''
                        'Check the thumbprint against the one your certificate authority publishes. Trust this certificate for sign-in?'
                    ) -join [Environment]::NewLine

                    if (-not (Request-JIMConfirmation -Cmdlet $PSCmdlet -Query $query -Caption 'Trust a certificate the identity provider presents')) {
                        return
                    }
                }
            }
            elseif (-not $PSCmdlet.ShouldProcess($description, "Trust for sign-in")) {
                return
            }

            Write-Verbose "Trusting the certificate the identity provider presents for sign-in: $description"
            try {
                $body = @{ thumbprint = $chosen }
                if ($Name) { $body.name = $Name }
                if ($ChangeReason) { $body.changeReason = $ChangeReason }

                $response = Invoke-JIMApi -Endpoint '/api/v1/certificates/identity-provider' -Method 'POST' -Body $body
                Write-Verbose "Outcome: $($response.outcome). $($response.message)"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to trust the identity provider's certificate: $_"
            }
            return
        }

        if ($PSCmdlet.ShouldProcess($Name, "Add Certificate")) {
            Write-Verbose "Adding certificate: $Name"

            try {
                $body = @{ name = $Name }
                $endpoint = '/api/v1/certificates/upload'

                switch ($PSCmdlet.ParameterSetName) {
                    'FromFile' {
                        $body.filePath = $Path
                        $endpoint = '/api/v1/certificates/file'
                    }
                    'FromData' { $body.certificateDataBase64 = [Convert]::ToBase64String($CertificateData) }
                    'FromBase64' { $body.certificateDataBase64 = $CertificateBase64 }
                }

                if ($Notes) { $body.notes = $Notes }
                if ($ChangeReason) { $body.changeReason = $ChangeReason }
                if ($TrustedFor) { $body.trustedFor = ($TrustedFor | Select-Object -Unique) -join ', ' }

                $response = Invoke-JIMApi -Endpoint $endpoint -Method 'POST' -Body $body

                Write-Verbose "Added certificate with ID: $($response.id)"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to add certificate: $_"
            }
        }
    }
}
