---
title: Certificates
---

# Certificates

**Certificates** store trusted CA and intermediate certificates used by [connectors](../connectors/index.md) for LDAP and HTTPS authentication. Each certificate can be enabled or disabled independently without removing it.

## Two source types

Certificates can be added in one of two ways:

- **Uploaded**<br /> The certificate data is supplied as Base64 and stored directly in the JIM database. Self-contained; survives container restarts without any external file dependency.
- **File path**<br /> The certificate is referenced by a path inside the connector-files volume mount. The file must remain accessible to JIM at runtime. Useful when an existing PKI tooling pipeline already places certificates on disk.

The two sources are equivalent at use time. The distinction matters mainly for deployment and rotation workflows: uploaded certificates travel with the JIM database, file-path certificates travel with the file mount.

## Enabled flag

Disabling a certificate removes it from the trust set without deleting it. Use this to temporarily revoke a CA without losing the metadata, or to stage a future change.

## Expiry awareness

JIM tracks `Valid From` and `Valid To` dates and surfaces convenience flags: `Is Expired`, `Is Expiring Soon`, and `Days Until Expiry`. Use these to drive monitoring and alerting; JIM does not auto-disable expired certificates, so they remain in the trust set unless you act on them.

## Validation

A separate validation operation lets you check a certificate's chain and validity before relying on it. It does not modify state and is safe to run before enabling a certificate or during routine review.

## Change history

Every change to the certificate store is recorded in [configuration change history](activities.md#configuration-change-history): adding, editing, enabling, disabling, or deleting a certificate captures a versioned snapshot of its metadata (name, thumbprint, subject, issuer, validity window, source, enabled state and notes) alongside who made the change, when, and an optional reason. The raw certificate material is never stored in the history; the thumbprint alone identifies the exact certificate, so a swap is always visible.

Open a certificate's history from the Change History action on its row, or retrieve it with `Get-JIMConfigurationChangeHistory -Type TrustedCertificate` or the REST API. The optional "Reason for change" prompt appears when uploading, adding, editing, or deleting a certificate in the admin portal; automation can pass the same reason via `-ChangeReason` on the certificate write cmdlets.

## Common workflows

**Adding a new CA certificate from your PKI:**

1. Either upload the Base64-encoded data, or place the file in the connector-files mount and reference it by path
2. Validate the certificate to confirm chain and validity
3. Confirm the certificate is enabled
4. Configure the relevant connector to authenticate using the Connected System's LDAPS or HTTPS endpoint

**Rotating a CA before expiry:**

1. Add the new certificate (uploaded or file path)
2. Validate it
3. Once you've confirmed connectors successfully negotiate against systems using the new CA, disable or delete the old certificate

## Manage Certificates

- **JIM portal**<br /> Certificates area of the admin UI
- **PowerShell**<br /> [Certificates cmdlets](../powershell/certificates.md) (`Get-JIMCertificate`, `New-JIMCertificate`, `Test-JIMCertificate`, etc.)
- **REST API**<br /> Certificates endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Connectors](../connectors/index.md) -- the connectors that use the certificate trust set
