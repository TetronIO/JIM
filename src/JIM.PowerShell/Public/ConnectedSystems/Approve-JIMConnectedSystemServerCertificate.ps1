# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Approve-JIMConnectedSystemServerCertificate {
    <#
    .SYNOPSIS
        Trusts the certificate a Connected System's server is presenting.

    .DESCRIPTION
        Reads the certificate from the server again, checks it against the thumbprint you supply, and adds
        it to the Trusted Certificates store through the audited path, so the addition carries an Activity
        naming who trusted it and why.

        The thumbprint is required. Reading again at the moment of the decision is what makes a certificate
        that changed since you looked at it detectable rather than waved through: if the server is
        presenting anything else, nothing is trusted and both thumbprints are reported.

        Any certificate in the server's chain can be trusted: the server's own, an intermediate certificate
        authority, or the root. Each makes the connection work; the higher in the chain, the more renewals
        the decision survives, so the root (rootThumbprint) is the one that does not have to be repeated.
        Read the chain with Get-JIMConnectedSystemServerCertificate. Certificate authorities JIM had to
        download, rather than the server sending them, are stored alongside the one you trust.

    .PARAMETER ConnectedSystemId
        The identifier of the Connected System whose server is asked.

    .PARAMETER Thumbprint
        The thumbprint being trusted, as read from the server. Matched against every certificate in the
        chain JIM assembles when it reads the server again; whichever matches is what gets trusted.
        Spaces and colons between the pairs are ignored.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the certificate's
        configuration change history. JIM records a sentence naming the Connected System when none is given.

    .PARAMETER SettingValues
        Connectivity settings entered but not yet saved, keyed by Connector Definition Setting identifier.
        Supply these when configuring a Connected System that cannot be saved yet; see
        Get-JIMConnectedSystemServerCertificate.

    .PARAMETER PassThru
        If specified, returns the outcome, including the certificate as it now sits in the store.

    .OUTPUTS
        If -PassThru is specified, returns a PSCustomObject with the outcome (Trusted, AlreadyTrusted,
        ThumbprintMismatch or InvalidChain), the certificate that was added, any certificate authorities
        stored alongside it (storedIntermediates), and the expected and presented thumbprints.

    .EXAMPLE
        Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01'

        Trusts the certificate, having checked that the server is still presenting the one named.

    .EXAMPLE
        $reading = Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
        $reading.certificate.chain | Format-Table subject, source, downloadedFrom
        Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
            -Thumbprint $reading.certificate.rootThumbprint `
            -ChangeReason 'Unblocking the HR Cloud connection test.'

        Looks at the chain the server presents, then trusts its root rather than the server's own
        certificate, so the decision survives renewal of everything beneath it. Check the thumbprint against the one
        the server's administrator gives you before running the second command.

    .EXAMPLE
        Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
            -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01' `
            -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2' } -PassThru

        Trusts the certificate presented by an endpoint that has been entered but not saved.

    .LINK
        Get-JIMConnectedSystemServerCertificate
        Get-JIMCertificate
        Remove-JIMCertificate
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        # Aliased so a Connected System object pipes in; the noun here is the certificate, not the system,
        # so -Id unambiguously means "the Connected System" (see src/JIM.PowerShell/CLAUDE.md).
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Thumbprint,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [hashtable]$SettingValues,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if (-not $PSCmdlet.ShouldProcess("Connected System $ConnectedSystemId", "Trust the server certificate $Thumbprint")) {
            return
        }

        Write-Verbose "Trusting the server certificate for Connected System: $ConnectedSystemId"

        try {
            $body = @{ thumbprint = $Thumbprint }
            if ($ChangeReason) { $body.changeReason = $ChangeReason }
            if ($PSBoundParameters.ContainsKey('SettingValues')) {
                $body.settingValues = ConvertTo-JIMSettingValueUpdates -SettingValues $SettingValues
            }

            $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/server-certificate/trust" -Method 'POST' -Body $body

            Write-Verbose "Outcome: $($response.outcome)"

            if ($PassThru) {
                $response
            }
        }
        catch {
            # A refused thumbprint is a considered outcome rather than a fault, so say what happened
            # rather than leaving the caller with a bare conflict status.
            Write-Error "Failed to trust the server certificate: $_"
        }
    }
}
