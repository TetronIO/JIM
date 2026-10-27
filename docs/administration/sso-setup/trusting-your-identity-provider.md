---
title: "Trust Your Identity Provider's Certificate"
description: "Let JIM sign people in through an identity provider whose TLS certificate comes from your organisation's own certificate authority."
type: how-to
audience: implementer
---

# Trust Your Identity Provider's Certificate

JIM connects to your identity provider over HTTPS to read its configuration and signing keys, and to complete each sign-in. When the identity provider's certificate comes from your organisation's own certificate authority, as it usually does for AD FS and for Keycloak run on premises, JIM does not trust it out of the box and nobody can sign in. Trust that certificate authority for sign-in in JIM's certificate store and sign-in works straight away: no image rebuild, no restart.

You'll know you need this page when any of these happen:

- JIM's log says `Sign-in is unavailable: JIM does not trust <host>'s certificate`, naming the certificate authority to trust.
- Opening JIM shows **Sign-in is unavailable**, with the certificate chain your identity provider presents and the command that fixes it.
- An administrator already signed in sees a banner saying **New sign-ins will fail**.

JIM keeps running while sign-in is unavailable: synchronisation, Schedules and API keys carry on, and nothing in the certificate store changes until someone trusts a certificate.

## :material-clipboard-list-outline: Before you begin

- **Who can do this**: an administrator signed in to the portal, or anyone holding an API key with the Administrator role. Before anyone can sign in, the infrastructure API key (`JIM_INFRASTRUCTURE_API_KEY`) is the way in; see [API keys](../../configuration/api-keys.md).
- **The PowerShell module**, if you follow the PowerShell steps; see [Install the PowerShell module](../../powershell/index.md).
- **The thumbprint your certificate authority publishes** for the certificate you will trust, to compare against the one JIM shows you. This check is what stops a certificate you did not expect being trusted.

## :material-certificate-outline: Trust the certificate

=== "PowerShell"

    1. Connect with the infrastructure API key:

        ```powershell
        Connect-JIM -Url https://jim.corp.contoso.com -ApiKey $env:JIM_INFRASTRUCTURE_API_KEY
        ```

    2. See what the identity provider presents:

        ```powershell
        Get-JIMIdentityProviderTrust
        (Get-JIMIdentityProviderTrust).presentedChain | Format-Table commonName, thumbprint, validTo
        ```

        `state` is `Untrusted`, and `suggestedThumbprint` names the highest certificate the identity provider sent, usually its issuing certificate authority.

    3. Compare that thumbprint with the one your certificate authority publishes, then trust it:

        ```powershell
        Add-JIMCertificate -FromIdentityProvider -Thumbprint '8F3A91D06B2E47C2C21D9F0E1A2B3C4D5E6F7A8B' -PassThru
        ```

        Without `-Thumbprint`, `Add-JIMCertificate -FromIdentityProvider` shows the suggested certificate and asks before trusting it.

=== "Portal"

    1. Sign in as an administrator. If nobody can sign in yet, use the PowerShell tab.
    2. On the banner that says **New sign-ins will fail**, select **Review and trust**.
    3. Choose the certificate to trust (the certificate authority is recommended), compare its thumbprint with the one your certificate authority publishes, and select **Trust for sign-in**.

=== "REST API"

    1. Read the status with `GET /api/v1/auth/identity-provider-trust`. It needs no sign-in, so it works when nobody can.
    2. Compare `suggestedThumbprint` with the one your certificate authority publishes.
    3. Trust it with `POST /api/v1/certificates/identity-provider`, sending `{ "thumbprint": "..." }` with the infrastructure API key as a bearer token.

JIM connects to the identity provider again for the request and trusts only a certificate it is presenting at that moment. A certificate that would not make the identity provider trusted, because its chain holds an expired certificate, is refused and nothing is stored.

### Trust the root instead

The identity provider rarely sends its root certificate authority, so the suggested certificate is usually the issuing one beneath it. The root outlasts every certificate it issues, so trusting it saves repeating this when the issuing certificate authority is renewed. The **Sign-in is unavailable** page and `unsentIssuer` from `Get-JIMIdentityProviderTrust` name it. Fetch it from your certificate authority and add it trusted for sign-in:

```powershell
Add-JIMCertificate -Name 'Contoso Root CA' -Path ./contoso-root.cer -TrustedFor SignIn
```

In the portal, upload it under **Admin** > **Certificates** with **Sign-in** ticked under **Trusted for**.

## :material-check-decagram-outline: Check it worked

1. A browser left on **Sign-in is unavailable** turns to **Sign-in is available** within 10 seconds, without reloading. Select **Sign in**.
2. `Get-JIMIdentityProviderTrust` reports `state` `Trusted` and `trustedBy` `TrustedByJimCertificateStore`.
3. Under **Admin** > **Certificates**, the certificate shows **Sign-in** under **Trusted for**, and **In use by sign-in** as its status.

## :material-shield-check-outline: What trusted for sign-in means

Every certificate in the store is trusted for Connectors, for sign-in, or both, and each only ever uses the certificates trusted for it. A certificate added for a Connected System's server never vouches for your identity provider: a certificate authority trusted for sign-in can vouch for the server that issues the tokens JIM signs people in with, so that is a decision on its own. A certificate already in the store for Connectors is trusted for sign-in as well when you trust it from the identity provider, rather than added twice.

Trust is only ever added. JIM still checks that the certificate names the identity provider's host, is in date and is properly signed; nothing in the store switches a check off. A certificate that does not name the host (`NameMismatch`) cannot be fixed by trusting anything: the identity provider needs a certificate that names the host in `JIM_SSO_AUTHORITY`, or the authority must use a name its certificate carries.

## :material-update: When the certificate changes

JIM checks the identity provider shortly after it starts and every 15 minutes after that.

- **Renewed onto a certificate authority JIM does not trust**: administrators who are signed in see **New sign-ins will fail** on every page, with **Review and trust**. Anyone already signed in can carry on until their session ends, which follows the identity provider's token lifetime, so trust the new certificate authority promptly. Everyone else sees **Sign-in is unavailable**, with the command filled in.
- **The certificate sign-in depends on expires within 30 days**: administrators see a notice naming it and the date. Trust its replacement before then.
- **Removing the certificate sign-in depends on**: deleting it, disabling it or unticking **Sign-in** stops everyone signing in, including you. The portal warns and asks you to type its name; `Set-JIMCertificate` and `Remove-JIMCertificate` warn and ask first. If it happens anyway, the infrastructure API key and the steps above put it right.

## :material-arrow-right-circle-outline: Next steps

- [Certificates](../../configuration/certificates.md): the certificate store, and what each certificate is trusted for.
- [Certificates cmdlets](../../powershell/certificates.md): `Get-JIMIdentityProviderTrust` and `Add-JIMCertificate -FromIdentityProvider` in full.
