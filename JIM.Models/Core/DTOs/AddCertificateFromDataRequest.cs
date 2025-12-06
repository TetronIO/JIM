namespace JIM.Models.Core.DTOs;

/// <summary>
/// Request to add a certificate by uploading the certificate data.
/// </summary>
public class AddCertificateFromDataRequest
{
    /// <summary>
    /// User-friendly name for the certificate.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Base64-encoded certificate data (PEM or DER format).
    /// </summary>
    public string CertificateDataBase64 { get; set; } = null!;

    /// <summary>
    /// Optional notes about the certificate.
    /// </summary>
    public string? Notes { get; set; }
}
