# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMCertificate {
    <#
    .SYNOPSIS
        Updates a certificate in JIM's trusted certificate store.

    .DESCRIPTION
        Updates a certificate's editable properties: name, notes, enabled status, and what it is trusted for. The
        certificate data itself cannot be changed.

        Disabling the only certificate sign-in depends on, or no longer trusting it for sign-in, stops everyone
        signing in to JIM. The cmdlet warns first and asks before going ahead, unless -Force is given.

    .PARAMETER Id
        The unique identifier (GUID) of the certificate to update.

    .PARAMETER Name
        New name for the certificate.

    .PARAMETER Notes
        New notes for the certificate.

    .PARAMETER Enable
        Enables the certificate for use.

    .PARAMETER Disable
        Disables the certificate without removing it.

    .PARAMETER TrustedFor
        What the certificate is trusted for: Connectors, SignIn, or both. Replaces what it was trusted for.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the certificate's
        Configuration Change History.

    .PARAMETER Force
        Goes ahead without asking when the change stops everyone signing in. The warning is still written.

    .PARAMETER PassThru
        Returns the certificate as updated.

    .OUTPUTS
        With -PassThru, the certificate as updated; otherwise nothing.

    .EXAMPLE
        Set-JIMCertificate -Id $certId -Name "New Name"

        Updates the certificate name.

    .EXAMPLE
        Set-JIMCertificate -Id $certId -Disable

        Disables the certificate.

    .EXAMPLE
        Get-JIMCertificate | Where-Object { $_.name -like "Test*" } | Set-JIMCertificate -Disable

        Disables all certificates with names starting with "Test".

    .EXAMPLE
        Set-JIMCertificate -Id $certId -TrustedFor SignIn, Connectors

        Trusts the certificate for sign-in as well as for Connectors.

    .LINK
        Get-JIMCertificate
        Add-JIMCertificate
        Remove-JIMCertificate
        Test-JIMCertificate
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType('JIM.TrustedCertificateDetailDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [string]$Name,

        [string]$Notes,

        [switch]$Enable,

        [switch]$Disable,

        [ValidateSet('Connectors', 'SignIn')]
        [string[]]$TrustedFor,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($Enable -and $Disable) {
            Write-Error "Cannot specify both -Enable and -Disable"
            return
        }

        # Disabling it, or no longer trusting it for sign-in, is the one edit that can stop everyone signing in; only
        # then is it worth asking JIM whether sign-in depends on this certificate. Warned before ShouldProcess, so a
        # -WhatIf preview says so too.
        $stopsSignIn = $null
        if ($Disable -or ($TrustedFor -and $TrustedFor -notcontains 'SignIn')) {
            try {
                $existing = Invoke-JIMApi -Endpoint "/api/v1/certificates/$Id"
            }
            catch {
                Write-Error "Certificate not found: $Id"
                return
            }

            if ($existing.signInDependsOnIt) {
                $stopsSignIn = $existing.name
                Write-Warning "Sign-in depends on '$stopsSignIn'. This change stops everyone signing in to JIM, including you. Until another certificate is trusted for sign-in, only the infrastructure API key can put it right."
            }
        }

        if ($PSCmdlet.ShouldProcess($Id, "Update Certificate")) {
            if ($stopsSignIn -and -not $Force -and
                -not (Request-JIMConfirmation -Cmdlet $PSCmdlet -Query "Stop sign-in through '$stopsSignIn'?" -Caption 'This change stops everyone signing in')) {
                return
            }

            Write-Verbose "Updating certificate: $Id"

            $body = @{}

            if ($Name) {
                $body.name = $Name
            }

            if ($PSBoundParameters.ContainsKey('Notes')) {
                $body.notes = $Notes
            }

            if ($Enable) {
                $body.isEnabled = $true
            } elseif ($Disable) {
                $body.isEnabled = $false
            }

            if ($TrustedFor) {
                $body.trustedFor = ($TrustedFor | Select-Object -Unique) -join ', '
            }

            if ($ChangeReason) {
                $body.changeReason = $ChangeReason
            }

            try {
                Invoke-JIMApi -Endpoint "/api/v1/certificates/$Id" -Method 'PATCH' -Body $body

                Write-Verbose "Updated certificate: $Id"

                if ($PassThru) {
                    # Fetch and return the updated certificate
                    Invoke-JIMApi -Endpoint "/api/v1/certificates/$Id"
                }
            }
            catch {
                Write-Error "Failed to update certificate: $_"
            }
        }
    }
}
