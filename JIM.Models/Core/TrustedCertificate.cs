using JIM.Models.Activities;
using JIM.Models.Interfaces;

namespace JIM.Models.Core;

/// <summary>
/// Represents a trusted CA certificate stored in the JIM certificate store.
/// Used by connectors for validating secure connections (LDAPS, HTTPS, etc.).
/// </summary>
public class TrustedCertificate : IAuditable
{
    /// <summary>
    /// Unique identifier for the certificate record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// User-friendly name for the certificate (e.g., "Corp Enterprise Root CA").
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// SHA-256 thumbprint/fingerprint of the certificate.
    /// Used for uniqueness checks and quick lookups.
    /// </summary>
    public string Thumbprint { get; set; } = null!;

    /// <summary>
    /// Certificate subject (CN, O, OU, etc.).
    /// </summary>
    public string Subject { get; set; } = null!;

    /// <summary>
    /// Certificate issuer distinguished name.
    /// </summary>
    public string Issuer { get; set; } = null!;

    /// <summary>
    /// Certificate serial number.
    /// </summary>
    public string SerialNumber { get; set; } = null!;

    /// <summary>
    /// Certificate validity start date (NotBefore).
    /// </summary>
    public DateTime ValidFrom { get; set; }

    /// <summary>
    /// Certificate expiry date (NotAfter).
    /// </summary>
    public DateTime ValidTo { get; set; }

    /// <summary>
    /// How the certificate was added to the store.
    /// </summary>
    public CertificateSourceType SourceType { get; set; }

    /// <summary>
    /// PEM or DER encoded certificate data.
    /// Only populated when SourceType is Uploaded.
    /// </summary>
    public byte[]? CertificateData { get; set; }

    /// <summary>
    /// Path to the certificate file relative to the connector-files mount.
    /// Only populated when SourceType is FilePath.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Whether this certificate is currently active and should be used for validation.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When the entity was created (UTC).
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity.
    /// Null for system-created (seeded) entities.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// Retained even if the principal is later deleted.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the entity was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// Optional notes about the certificate (e.g., purpose, renewal info).
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Indicates whether the certificate has expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow > ValidTo;

    /// <summary>
    /// Indicates whether the certificate will expire within 30 days.
    /// </summary>
    public bool IsExpiringSoon => !IsExpired && DateTime.UtcNow > ValidTo.AddDays(-30);

    /// <summary>
    /// Number of days until the certificate expires (negative if already expired).
    /// </summary>
    public int DaysUntilExpiry => (int)(ValidTo - DateTime.UtcNow).TotalDays;
}
