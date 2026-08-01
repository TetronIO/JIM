# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemServerCertificate {
    <#
    .SYNOPSIS
        Reads the certificate a Connected System's server is presenting.

    .DESCRIPTION
        Connects to the endpoint the Connected System is configured for, purely to look at the certificate
        the server offers, and refuses it. Nothing is stored: trusting the certificate is a separate,
        explicit call to Approve-JIMConnectedSystemServerCertificate.

        The endpoint is always worked out by the Connected System's own connector from that system's
        settings; it is never named directly, so this cannot be used to make JIM connect to an address of
        your choosing.

    .PARAMETER ConnectedSystemId
        The identifier of the Connected System whose server is asked.

    .PARAMETER SettingValues
        Connectivity settings entered but not yet saved, keyed by Connector Definition Setting identifier.
        Supply these when configuring a Connected System that cannot be saved yet: JIM does not save
        settings that fail validation, and a certificate JIM does not trust is a validation failure, so
        without them JIM would look at the endpoint last saved rather than the one being configured.

        The setting identifiers come from Get-JIMConnectorDefinition. Values are never persisted, and
        values for encrypted settings are ignored.

    .OUTPUTS
        PSCustomObject with the certificate the server presented (subject, issuer, the names it was issued
        for, validity dates, thumbprint, whether it is self-signed, which check it fails and what to do
        about it) and when it was read.

    .EXAMPLE
        Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42

        Reads the certificate the configured server is presenting.

    .EXAMPLE
        Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 |
            Select-Object -ExpandProperty certificate |
            Select-Object host, subject, thumbprint, failureReason

        Shows the identifying details and which check the certificate fails.

    .EXAMPLE
        Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2' }

        Reads the certificate presented by an endpoint that has been entered but not saved, using the
        Connector Definition Setting identifier for that connector's Base URL.

    .LINK
        Approve-JIMConnectedSystemServerCertificate
        Get-JIMConnectedSystem
        Get-JIMConnectorDefinition
        Get-JIMCertificate
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        # Aliased so a Connected System object pipes in; the noun here is the certificate, not the system,
        # so -Id unambiguously means "the Connected System" (see src/JIM.PowerShell/CLAUDE.md).
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [hashtable]$SettingValues
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Reading the server certificate for Connected System: $ConnectedSystemId"

        try {
            $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/server-certificate"

            if ($PSBoundParameters.ContainsKey('SettingValues')) {
                # Unsaved settings cannot travel on a GET, so the same read is available as a POST.
                $body = @{ settingValues = ConvertTo-JIMSettingValueUpdates -SettingValues $SettingValues }
                Invoke-JIMApi -Endpoint $endpoint -Method 'POST' -Body $body
            }
            else {
                Invoke-JIMApi -Endpoint $endpoint
            }
        }
        catch {
            Write-Error "Failed to read the server certificate: $_"
        }
    }
}
