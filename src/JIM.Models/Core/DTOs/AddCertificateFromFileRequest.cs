using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Request to add a certificate by referencing a file path.
/// </summary>
public class AddCertificateFromFileRequest
{
    /// <summary>
    /// User-friendly name for the certificate.
    /// </summary>
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(255, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 255 characters.")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Path to the certificate file (relative to connector-files mount or absolute path).
    /// </summary>
    [Required(ErrorMessage = "File path is required.")]
    [StringLength(4096, MinimumLength = 1, ErrorMessage = "File path must be between 1 and 4096 characters.")]
    public string FilePath { get; set; } = null!;

    /// <summary>
    /// Optional notes about the certificate.
    /// </summary>
    [StringLength(2000, ErrorMessage = "Notes must not exceed 2000 characters.")]
    public string? Notes { get; set; }
}
