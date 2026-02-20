using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Request to add a certificate by uploading the certificate data.
/// </summary>
public class AddCertificateFromDataRequest
{
    /// <summary>
    /// User-friendly name for the certificate.
    /// </summary>
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(255, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 255 characters.")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Base64-encoded certificate data (PEM or DER format).
    /// </summary>
    [Required(ErrorMessage = "Certificate data is required.")]
    [StringLength(1048576, ErrorMessage = "Certificate data must not exceed 1MB.")] // 1MB limit for base64 data
    public string CertificateDataBase64 { get; set; } = null!;

    /// <summary>
    /// Optional notes about the certificate.
    /// </summary>
    [StringLength(2000, ErrorMessage = "Notes must not exceed 2000 characters.")]
    public string? Notes { get; set; }
}
