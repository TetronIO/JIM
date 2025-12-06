namespace JIM.Models.Core.DTOs;

/// <summary>
/// Request to add a certificate by referencing a file path.
/// </summary>
public class AddCertificateFromFileRequest
{
    /// <summary>
    /// User-friendly name for the certificate.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Path to the certificate file (relative to connector-files mount or absolute path).
    /// </summary>
    public string FilePath { get; set; } = null!;

    /// <summary>
    /// Optional notes about the certificate.
    /// </summary>
    public string? Notes { get; set; }
}
