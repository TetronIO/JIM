---
title: Certificates
---

# Certificates

Certificates store trusted CA and intermediate certificates used by connectors for LDAP and HTTPS authentication. Each certificate can be enabled or disabled independently without removing it.

> Endpoint reference for this resource is in the [interactive API reference](../index.md#where-to-find-what). This page covers the storage model and operational behaviour.

## Key Concepts

**Two source types.** Certificates can be added in one of two ways:

- **Uploaded** -- the certificate data is supplied as Base64 and stored directly in the JIM database. Self-contained; survives container restarts without any external file dependency.
- **File path** -- the certificate is referenced by a path inside the connector-files volume mount. The file must remain accessible to JIM at runtime. Useful when an existing PKI tooling pipeline already places certificates on disk.

The two sources are equivalent at use time; the distinction matters mainly for deployment and rotation workflow.

**Enabled flag.** Disabling a certificate removes it from the trust set without deleting it. Use this to temporarily revoke a CA without losing the metadata, or to stage a future change.

**Expiry awareness.** JIM tracks `validFrom` / `validTo` and surfaces convenience flags (`isExpired`, `isExpiringSoon`, `daysUntilExpiry`). Use these to drive monitoring; the API does not auto-disable expired certificates.

**Validation.** A separate validation operation lets you check a certificate's chain and validity before relying on it. It does not modify state.

## Common Workflows

**Adding a new CA certificate from your PKI:**

1. Either upload the Base64-encoded data, or place the file in the connector-files mount and reference it by path
2. Validate the certificate to confirm chain and validity
3. Confirm the certificate is enabled and visible in the enabled list
4. Configure the relevant connector to authenticate using the connected system's LDAPS/HTTPS endpoint

**Rotating a CA before expiry:**

1. Add the new certificate (uploaded or file path)
2. Validate
3. Once you've confirmed connectors successfully negotiate against systems using the new CA, disable or delete the old certificate

## See also

- [Connectors](../../connectors/index.md) -- which connectors use the certificate trust set
- [PowerShell: Certificates](../../powershell/certificates.md) -- cmdlets that wrap these endpoints
