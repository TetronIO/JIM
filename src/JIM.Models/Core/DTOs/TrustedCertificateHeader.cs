namespace JIM.Models.Core.DTOs;

/// <summary>
/// Lightweight representation of a trusted certificate for list views.
/// </summary>
public class TrustedCertificateHeader
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Thumbprint { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string Issuer { get; set; } = null!;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public CertificateSourceType SourceType { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsExpired { get; set; }
    public bool IsExpiringSoon { get; set; }
    public int DaysUntilExpiry { get; set; }

    public static TrustedCertificateHeader FromEntity(TrustedCertificate cert)
    {
        return new TrustedCertificateHeader
        {
            Id = cert.Id,
            Name = cert.Name,
            Thumbprint = cert.Thumbprint,
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            ValidFrom = cert.ValidFrom,
            ValidTo = cert.ValidTo,
            SourceType = cert.SourceType,
            IsEnabled = cert.IsEnabled,
            IsExpired = cert.IsExpired,
            IsExpiringSoon = cert.IsExpiringSoon,
            DaysUntilExpiry = cert.DaysUntilExpiry
        };
    }
}
