---
title: Certificates
description: "Add the certificates JIM trusts, for Connectors, for sign-in, or both, uploaded or from a file, and track when each expires."
type: object
audience: administrator
---

# Certificates

**Certificates** store the certificate authorities, and server certificates, that JIM trusts: for [Connectors](../connectors/index.md)' LDAPS, HTTPS and SQL Server connections, for signing in through your identity provider, or both. Each certificate can be enabled or disabled independently without removing it.

Certificates added here are trusted **in addition to** the operating system's own trust store, never in place of it, so adding one can only ever allow more connections to succeed. A directory server's own self-signed certificate can be added directly, not just the certificate authority that issued it. Certificate validation itself cannot be switched off for an individual Connected System; see [certificate validation](../connectors/jim-ldap-connector-certificates.md#certificate-validation) in the LDAP Connector documentation for what is checked.

## :material-shield-check-outline: Trusted for

Each certificate is trusted for **Connectors**, for **Sign-in**, or both, and each purpose only ever uses the certificates trusted for it.

- **Connectors**<br /> The servers Connected Systems connect to. This is the default for a certificate you add.
- **Sign-in**<br /> Your identity provider, when its certificate comes from your organisation's own certificate authority. See [Trust Your Identity Provider's Certificate](../administration/sso-setup/trusting-your-identity-provider.md).

A certificate added for a Connected System never vouches for your identity provider, because a certificate authority trusted for sign-in can vouch for the server that issues the tokens JIM signs people in with. Trust a certificate for sign-in only when that is what you mean. The certificate sign-in relies on shows **In use by sign-in**; deleting it, disabling it or unticking **Sign-in** stops everyone signing in, so the portal and the cmdlets warn first.

## :material-file-search-outline: Two source types

Certificates can be added in one of two ways:

- **Uploaded**<br /> The certificate data is supplied as Base64 and stored directly in the JIM database. Self-contained; survives container restarts without any external file dependency.
- **File path**<br /> The certificate is referenced by a path inside the connector-files volume mount. The file must remain accessible to JIM at runtime. Useful when an existing PKI tooling pipeline already places certificates on disk.

The two sources are equivalent at use time. The distinction matters mainly for deployment and rotation workflows: uploaded certificates travel with the JIM database, file-path certificates travel with the file mount.

## :material-toggle-switch-outline: Enabled flag

Disabling a certificate removes it from the trust set without deleting it. Use this to temporarily revoke a CA without losing the metadata, or to stage a future change.

## :material-timer-outline: Expiry awareness

JIM tracks `Valid From` and `Valid To` dates and surfaces convenience flags: `Is Expired`, `Is Expiring Soon`, and `Days Until Expiry`. Use these to drive monitoring and alerting; JIM does not auto-disable expired certificates, so they remain in the trust set unless you act on them.

## :material-eye-check-outline: Validation

A separate validation operation lets you check a certificate's chain and validity before relying on it. It does not modify state and is safe to run before enabling a certificate or during routine review.

## :material-history: Change history

Every change to the certificate store is recorded in [Configuration Change History](activities.md#configuration-change-history): adding, editing, enabling, disabling, or deleting a certificate captures a versioned snapshot of its metadata (name, thumbprint, subject, issuer, validity window, source, enabled state, what it is trusted for, and notes) alongside who made the change, when, and an optional reason. The raw certificate material is never stored in the history; the thumbprint alone identifies the exact certificate, so a swap is always visible.

Open a certificate's history from the Change History action on its row, or retrieve it with `Get-JIMConfigurationChangeHistory -Type TrustedCertificate` or the REST API. The optional "Reason for change" prompt appears when uploading, adding, editing, or deleting a certificate in the admin portal; automation can pass the same reason via `-ChangeReason` on the certificate write cmdlets.

## :material-playlist-check: Common workflows

**Adding a new CA certificate from your PKI:**

1. Either upload the Base64-encoded data, or place the file in the connector-files mount and reference it by path
2. Validate the certificate to confirm chain and validity
3. Confirm the certificate is enabled
4. Configure the relevant connector to authenticate using the Connected System's LDAPS or HTTPS endpoint

**Rotating a CA before expiry:**

1. Add the new certificate (uploaded or file path)
2. Validate it
3. Once you've confirmed connectors successfully negotiate against systems using the new CA, disable or delete the old certificate

## :material-cog-outline: Manage Certificates

- **JIM portal**<br /> Certificates area of the admin UI
- **PowerShell**<br /> [Certificates cmdlets](../powershell/certificates.md) (`Get-JIMCertificate`, `Add-JIMCertificate`, `Test-JIMCertificate`, etc.)
- **REST API**<br /> Certificates endpoints in the [interactive API reference](../../api/reference/)

## :material-book-open-variant-outline: Related

- [Connectors](../connectors/index.md): the connectors that use the certificate trust set
- [Trust Your Identity Provider's Certificate](../administration/sso-setup/trusting-your-identity-provider.md): trusting a certificate for sign-in
