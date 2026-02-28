# JIM Certificate Store

- **Status:** Done

> Centralised certificate management for secure connector communications

## Overview

The JIM Certificate Store provides centralised management of trusted CA certificates used by connectors for secure communications (LDAPS, HTTPS, etc.). This eliminates the need to configure certificates per-connector and simplifies certificate rotation.

## Problem Statement

When JIM runs in Docker containers:
- Containers only include public CA root certificates by default
- Enterprise/internal CAs are not trusted
- Self-signed certificates fail validation
- Per-connector certificate configuration is error-prone and hard to maintain

## Solution

A centralised certificate store that:
- Stores trusted CA certificates (root and intermediate)
- Supports both uploaded certificates and file path references
- Automatically validates certificates used by all connectors
- Provides management UI and API for certificate lifecycle

---

## Architecture

```
+-------------------------------------------------------------+
|                   JIM Certificate Store                     |
|  +---------------+ +---------------+ +---------------+      |
|  |  Enterprise   | |  Intermediate | |   Partner     |      |
|  |   Root CA     | |      CA       | |   Root CA     |      |
|  |  (uploaded)   | |  (uploaded)   | |  (file path)  |      |
|  +---------------+ +---------------+ +---------------+      |
+-------------------------------------------------------------+
                              |
              +---------------+---------------+
              |               |               |
              v               v               v
       +----------+    +----------+    +----------+
       |   LDAP   |    |   REST   |    | Database |
       |Connector |    |Connector |    |Connector |
       +----------+    +----------+    +----------+
```

---

## Data Model

### TrustedCertificate Entity

```csharp
public class TrustedCertificate
{
    public Guid Id { get; set; }

    /// <summary>
    /// User-friendly name for the certificate (e.g., "Corp Enterprise Root CA")
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// SHA-256 thumbprint/fingerprint of the certificate
    /// </summary>
    public string Thumbprint { get; set; } = null!;

    /// <summary>
    /// Certificate subject (CN, O, OU, etc.)
    /// </summary>
    public string Subject { get; set; } = null!;

    /// <summary>
    /// Certificate issuer
    /// </summary>
    public string Issuer { get; set; } = null!;

    /// <summary>
    /// Certificate serial number
    /// </summary>
    public string SerialNumber { get; set; } = null!;

    /// <summary>
    /// Certificate validity start date
    /// </summary>
    public DateTime ValidFrom { get; set; }

    /// <summary>
    /// Certificate expiry date
    /// </summary>
    public DateTime ValidTo { get; set; }

    /// <summary>
    /// Source type: Uploaded or FilePath
    /// </summary>
    public CertificateSourceType SourceType { get; set; }

    /// <summary>
    /// PEM or DER encoded certificate data (for uploaded certificates)
    /// </summary>
    public byte[]? CertificateData { get; set; }

    /// <summary>
    /// Path to certificate file in connector-files mount (for file-based certificates)
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Whether this certificate is currently active/trusted
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When the certificate was added to the store
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// User who added the certificate
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Optional notes about the certificate
    /// </summary>
    public string? Notes { get; set; }
}

public enum CertificateSourceType
{
    Uploaded = 0,
    FilePath = 1
}
```

---

## API Endpoints

### Certificate Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/certificates` | List all trusted certificates |
| `GET` | `/api/certificates/{id}` | Get certificate details |
| `POST` | `/api/certificates/upload` | Upload a certificate file |
| `POST` | `/api/certificates/filepath` | Add certificate by file path |
| `PUT` | `/api/certificates/{id}` | Update certificate (name, notes, enabled) |
| `DELETE` | `/api/certificates/{id}` | Remove certificate from store |
| `GET` | `/api/certificates/{id}/validate` | Validate certificate (check chain, expiry) |
| `GET` | `/api/certificates/{id}/download` | Download certificate (PEM format) |

### Request/Response DTOs

```csharp
// Upload request - multipart/form-data
public class UploadCertificateRequest
{
    public string Name { get; set; } = null!;
    public IFormFile CertificateFile { get; set; } = null!;
    public string? Notes { get; set; }
}

// File path request
public class AddCertificateByPathRequest
{
    public string Name { get; set; } = null!;
    public string FilePath { get; set; } = null!;
    public string? Notes { get; set; }
}

// Response
public class CertificateDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Thumbprint { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string Issuer { get; set; } = null!;
    public string SerialNumber { get; set; } = null!;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public CertificateSourceType SourceType { get; set; }
    public string? FilePath { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? Notes { get; set; }

    // Computed properties
    public bool IsExpired => DateTime.UtcNow > ValidTo;
    public bool IsExpiringSoon => DateTime.UtcNow > ValidTo.AddDays(-30);
    public int DaysUntilExpiry => (int)(ValidTo - DateTime.UtcNow).TotalDays;
}

// Validation response
public class CertificateValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
```

---

## Web UI

### Navigation

Settings -> Security -> Trusted Certificates

### Certificate List View

```
+-----------------------------------------------------------------------------+
| Trusted Certificates                                    [Upload] [Add Path] |
+-----------------------------------------------------------------------------+
| Name                 | Subject        | Issuer    | Expires    | Source | : |
+----------------------+----------------+-----------+------------+--------+---+
| Enterprise Root      | CN=Corp Root   | Self      | 2030-01-15 | [U]    | : |
| Intermediate CA      | CN=Corp Sub CA | Corp Root | 2027-06-30 | [U]    | : |
| [!] Partner CA       | CN=Partner     | Self      | 2025-01-20 | [F]    | : |
+----------------------+----------------+-----------+------------+--------+---+

Legend: [U] = Uploaded, [F] = File Path, [!] = Expiring Soon
```

### Upload Dialog

```
+-- Upload Certificate ----------------------------------------+
|                                                              |
|  Name *                                                      |
|  +--------------------------------------------------------+  |
|  | Enterprise Root CA                                     |  |
|  +--------------------------------------------------------+  |
|                                                              |
|  Certificate File *                                          |
|  +--------------------------------------------------------+  |
|  | enterprise-root-ca.crt                    [Browse...]  |  |
|  +--------------------------------------------------------+  |
|  Supported formats: .pem, .crt, .cer, .der                   |
|                                                              |
|  Notes                                                       |
|  +--------------------------------------------------------+  |
|  | Main enterprise PKI root certificate                   |  |
|  +--------------------------------------------------------+  |
|                                                              |
|                                  [Cancel]  [Upload]          |
+--------------------------------------------------------------+
```

### Add by File Path Dialog

```
+-- Add Certificate by File Path -----------------------------+
|                                                             |
|  Name *                                                     |
|  +-------------------------------------------------------+  |
|  | Partner Root CA                                       |  |
|  +-------------------------------------------------------+  |
|                                                             |
|  File Path *                                                |
|  +-------------------------------------------------------+  |
|  | /connector-files/certs/partner-ca.pem                 |  |
|  +-------------------------------------------------------+  |
|  Path relative to connector-files mount                     |
|                                                             |
|  Notes                                                      |
|  +-------------------------------------------------------+  |
|  | Certificate for partner B2B integration               |  |
|  +-------------------------------------------------------+  |
|                                                             |
|                                  [Cancel]  [Add]            |
+-------------------------------------------------------------+
```

---

## Connector Integration

### ICertificateStore Interface

```csharp
public interface ICertificateStore
{
    /// <summary>
    /// Gets all enabled trusted certificates as X509Certificate2 objects
    /// </summary>
    Task<IReadOnlyList<X509Certificate2>> GetTrustedCertificatesAsync();

    /// <summary>
    /// Validates a server certificate against the JIM certificate store
    /// </summary>
    bool ValidateCertificate(X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors);
}
```

### Connector Usage

```csharp
// In LdapConnector.cs
public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
{
    // ... existing code ...

    if (useSsl)
    {
        _connection.SessionOptions.SecureSocketLayer = true;

        if (skipCertValidation)
        {
            logger.Warning("Certificate validation is disabled.");
            _connection.SessionOptions.VerifyServerCertificate = (conn, cert) => true;
        }
        else
        {
            // Use JIM certificate store for validation
            _connection.SessionOptions.VerifyServerCertificate = (conn, cert) =>
                _certificateStore.ValidateCertificate(cert, null, SslPolicyErrors.None);
        }
    }
}
```

### Certificate Validation Logic

```csharp
public bool ValidateCertificate(X509Certificate certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
{
    // If no SSL errors from system validation, certificate is already trusted
    if (sslPolicyErrors == SslPolicyErrors.None)
        return true;

    // Only handle chain errors (untrusted root/intermediate)
    if (!sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        return false;

    // Build chain with JIM trusted certificates
    using var x509Cert = new X509Certificate2(certificate);
    using var customChain = new X509Chain();

    // Add JIM trusted certificates to chain
    foreach (var trustedCert in GetTrustedCertificatesAsync().GetAwaiter().GetResult())
    {
        customChain.ChainPolicy.ExtraStore.Add(trustedCert);
    }

    customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

    if (!customChain.Build(x509Cert))
    {
        // Check if the root is in our trusted store
        var root = customChain.ChainElements[^1].Certificate;
        return GetTrustedCertificatesAsync().GetAwaiter().GetResult()
            .Any(tc => tc.Thumbprint.Equals(root.Thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    return true;
}
```

---

## Connector Setup Wizard Integration

When a user enables LDAPS (or other secure connections) and no certificates are configured:

```
+-- LDAP Connector Setup ------------------------------------------+
|                                                                  |
|  Connection Settings                                             |
|  -------------------                                             |
|  Host: dc01.corp.local                                           |
|  Port: 636                                                       |
|  [x] Use Secure Connection (LDAPS)                               |
|                                                                  |
|  +-----------------------------------------------------------+   |
|  | [!] Certificate Trust Required                            |   |
|  |                                                           |   |
|  | No trusted certificates are configured in JIM.            |   |
|  | Your directory server's SSL certificate must be           |   |
|  | trusted for secure connections to work.                   |   |
|  |                                                           |   |
|  | [Configure Certificates]  [Skip Validation]               |   |
|  |                                                           |   |
|  | (i) Skipping validation is not recommended for            |   |
|  |     production environments (vulnerable to MITM).         |   |
|  +-----------------------------------------------------------+   |
|                                                                  |
|                                      [Back]  [Test Connection]   |
+------------------------------------------------------------------+
```

---

## Implementation Plan

### Phase 1: Foundation
- [ ] Create `TrustedCertificate` entity in `src/JIM.Models/Core/`
- [ ] Create `CertificateSourceType` enum
- [ ] Add DbSet to `JimDbContext`
- [ ] Create database migration
- [ ] Create `ITrustedCertificateRepository` interface
- [ ] Implement repository in `JIM.PostgresData`

### Phase 2: Application Service
- [ ] Create `ICertificateService` interface in `src/JIM.Models/Interfaces/`
- [ ] Create `CertificateService` in `src/JIM.Application/Services/`
- [ ] Implement certificate parsing (PEM, DER, CRT)
- [ ] Implement certificate validation
- [ ] Implement file path certificate loading
- [ ] Add to `JimApplication` facade

### Phase 3: API Layer
- [ ] Create DTOs in `src/JIM.Web/Models/Api/`
- [ ] Create `CertificatesController`
- [ ] Implement all CRUD endpoints
- [ ] Add file upload handling
- [ ] Add Swagger documentation

### Phase 4: Web UI
- [ ] Create `Certificates.razor` page
- [ ] Create certificate list component
- [ ] Create upload dialog component
- [ ] Create file path dialog component
- [ ] Add to navigation menu
- [ ] Add expiry warning indicators

### Phase 5: Connector Integration
- [ ] Create `ICertificateStore` interface
- [ ] Implement `CertificateStore` service
- [ ] Update `LdapConnector` to use certificate store
- [ ] Update connector setup wizard
- [ ] Add certificate store to DI container in Worker

### Phase 6: Testing
- [ ] Unit tests for `CertificateService`
- [ ] Unit tests for certificate parsing
- [ ] Integration tests for API endpoints
- [ ] UI component tests

---

## Security Considerations

1. **Certificate Data Storage**: Certificate data (public keys only) stored in database is not sensitive, but ensure database access is properly secured.

2. **File Path Access**: File paths must be validated to prevent directory traversal attacks. Only allow paths within the connector-files mount.

3. **Certificate Validation**: When validating, check:
   - Certificate is not expired
   - Certificate is not revoked (optional, configurable)
   - Certificate chain is valid

4. **Audit Logging**: Log all certificate store modifications (add, remove, enable/disable).

---

## Docker Compose Configuration

```yaml
services:
  jim.worker:
    volumes:
      - ./connector-files:/connector-files:ro
    environment:
      - JIM_CONNECTOR_FILES_PATH=/connector-files
```

Users can place certificates in `./connector-files/certs/` and reference them as `/connector-files/certs/my-ca.pem`.

---

## Future Enhancements

1. **Certificate Expiry Notifications**: Email/webhook alerts when certificates are expiring
2. **Automatic Renewal**: Integration with ACME/Let's Encrypt for auto-renewal
3. **Certificate Revocation Checking**: OCSP/CRL checking
4. **Certificate Groups**: Organise certificates by purpose or environment
5. **PowerShell Module**: `Get-JimCertificate`, `Add-JimCertificate`, `Remove-JimCertificate`

---

## References

- [X509Certificate2 Class](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509certificate2)
- [X509Chain Class](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chain)
- [SSL/TLS Best Practices](https://wiki.mozilla.org/Security/Server_Side_TLS)
