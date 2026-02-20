using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// Detailed API representation of a TrustedCertificate (excludes raw certificate bytes for security).
/// </summary>
public class TrustedCertificateDetailDto
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
    public bool IsExpired { get; set; }
    public bool IsExpiringSoon { get; set; }
    public int DaysUntilExpiry { get; set; }

    /// <summary>
    /// Creates a detailed DTO from a TrustedCertificate entity.
    /// Note: CertificateData is intentionally excluded for security.
    /// </summary>
    public static TrustedCertificateDetailDto FromEntity(TrustedCertificate entity)
    {
        return new TrustedCertificateDetailDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Thumbprint = entity.Thumbprint,
            Subject = entity.Subject,
            Issuer = entity.Issuer,
            SerialNumber = entity.SerialNumber,
            ValidFrom = entity.ValidFrom,
            ValidTo = entity.ValidTo,
            SourceType = entity.SourceType,
            FilePath = entity.FilePath,
            IsEnabled = entity.IsEnabled,
            CreatedAt = entity.Created,
            CreatedBy = entity.CreatedByName,
            Notes = entity.Notes,
            IsExpired = entity.IsExpired,
            IsExpiringSoon = entity.IsExpiringSoon,
            DaysUntilExpiry = entity.DaysUntilExpiry
        };
    }
}
