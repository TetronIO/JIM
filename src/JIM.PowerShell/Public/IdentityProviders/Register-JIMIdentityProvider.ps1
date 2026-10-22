# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Register-JIMIdentityProvider {
    <#
    .SYNOPSIS
        Registers JIM with its identity provider, or repairs the registration.

    .DESCRIPTION
        Creates everything JIM needs on its identity provider, exactly as the SSO setup guide describes, and corrects
        whatever has drifted from it. Running it again changes nothing when the registration is already right, so it
        is also the way to check and repair it: -WhatIf lists each change it would make, and makes none.

        For AD FS it creates an Application Group holding the portal (a Server application, with a client secret), the
        PowerShell module (a Native application, with loopback redirect URIs for ports 8400 to 8409) and JIM's API (a
        Web API open to everyone, whose one claim rule issues each person's objectGUID as oid with their name and
        email). Only JIM's two clients may ask for the API. AD FS decides who someone is; what they may do is decided
        by their roles in JIM.

        AD FS is configured through the ADFS PowerShell module, which only an AD FS server has. Run this on the
        federation server, or pass -Session with a PowerShell session to it. This command does not connect to JIM, so
        Connect-JIM is not needed.

        The client secret is returned, as a secure string, only when this run created or replaced it. AD FS shows a
        secret once, so keep it then; if it is lost, run again with -ResetClientSecret.

    .PARAMETER Provider
        The identity provider to register JIM with.

    .PARAMETER Url
        JIM's address as people reach it, such as https://jim.example.com, with the port if it is not 443. The portal's
        sign-in and sign-out redirect URIs are made from it.

    .PARAMETER Name
        The name of the AD FS Application Group, which is also its identifier. The applications in it are named after
        it, with " - Portal", " - PowerShell" and " - API".

    .PARAMETER Session
        A PowerShell session to the AD FS server, from New-PSSession. When omitted, the command runs on this computer,
        which must be an AD FS server. The session is left open.

    .PARAMETER ResetClientSecret
        Replaces the portal's client secret, and returns the new one. JIM cannot sign anyone in until JIM_SSO_SECRET
        is given the new secret.

    .OUTPUTS
        JIM.IdentityProviderRegistration
        Provider (string): The identity provider, ADFS.
        Authority (string): The value for JIM_SSO_AUTHORITY.
        ValidIssuers (string[]): The value for JIM_SSO_VALID_ISSUERS, joined with commas. AD FS issues access tokens under its federation service identifier, not the authority.
        ClientId (string): The portal's client identifier, the value for JIM_SSO_CLIENT_ID.
        ClientSecret (securestring): The portal's client secret, the value for JIM_SSO_SECRET. Empty unless this run created or replaced it.
        PublicClientId (string): The PowerShell module's client identifier, the value for JIM_SSO_PUBLIC_CLIENT_ID.
        ApiScope (string): The API's identifier, the value for JIM_SSO_API_SCOPE.
        ClaimType (string): The claim JIM identifies people by, the value for JIM_SSO_CLAIM_TYPE.
        MvAttribute (string): The Metaverse attribute that claim is matched with, the value for JIM_SSO_MV_ATTRIBUTE.
        Changes (string[]): The changes this run made. Empty when the registration was already right.

        With -WhatIf, nothing is returned.

    .EXAMPLE
        $registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com'

        Run on the AD FS server: registers JIM, or repairs its registration, and keeps what JIM needs.

    .EXAMPLE
        $session = New-PSSession -ComputerName 'adfs1.example.com'
        Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Session $session -WhatIf

        Lists what is missing or has drifted on a remote AD FS server, and changes nothing.

    .EXAMPLE
        $registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -ResetClientSecret
        [System.Net.NetworkCredential]::new('', $registration.ClientSecret).Password | Set-Clipboard

        Replaces the portal's client secret and copies the new one to the clipboard, for JIM_SSO_SECRET.

    .LINK
        Get-JIMIdentityProviderTrust
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.IdentityProviderRegistration')]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('ADFS')]
        [string]$Provider,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Url,

        [ValidateNotNullOrEmpty()]
        [string]$Name = 'JIM Identity Management',

        [System.Management.Automation.Runspaces.PSSession]$Session,

        [switch]$ResetClientSecret
    )

    $uri = $null
    if (-not [uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
        throw "The JIM URL must be an absolute https address, such as https://jim.example.com, not '$Url'."
    }

    $ownSession = -not $Session
    $activeSession = if ($ownSession) { New-JIMAdfsSession } else { $Session }
    try {
        $current = Get-JIMAdfsRegistrationState -Session $activeSession -Name $Name
        $plan = Get-JIMAdfsRegistrationPlan -Url $Url -Name $Name -Current $current -ResetClientSecret:$ResetClientSecret

        $made = [System.Collections.Generic.List[string]]::new()
        $secret = $null
        $declined = $false
        foreach ($change in $plan.Changes) {
            if (-not $PSCmdlet.ShouldProcess($change.Target, $change.Description)) {
                $declined = $true
                continue
            }
            $result = Invoke-JIMAdfsRegistrationChange -Session $activeSession -Change $change
            if ($result) { $secret = ConvertTo-SecureString -String ([string]$result) -AsPlainText -Force }
            $made.Add("$($change.Target): $($change.Description)")
        }

        # Values are only worth returning once they exist on AD FS.
        if ($declined) { return }

        $authority = "https://$($current.HostName)/adfs"
        $result = [PSCustomObject]@{
            Provider       = $Provider
            Authority      = $authority
            ValidIssuers   = @($authority, $current.Identifier | Where-Object { $_ } | Select-Object -Unique)
            ClientId       = $plan.ClientId
            ClientSecret   = $secret
            PublicClientId = $plan.PublicClientId
            ApiScope       = $plan.ApiIdentifier
            ClaimType      = 'oid'
            MvAttribute    = 'Object GUID'
            Changes        = $made.ToArray()
        }
        $result.PSObject.TypeNames.Insert(0, 'JIM.IdentityProviderRegistration')
        $result
    }
    finally {
        if ($ownSession -and $activeSession) { Remove-PSSession -Session $activeSession }
    }
}
