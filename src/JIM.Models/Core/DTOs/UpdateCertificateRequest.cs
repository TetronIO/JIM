using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Request to update a certificate's editable properties.
/// </summary>
public class UpdateCertificateRequest
{
    /// <summary>
    /// New name for the certificate (optional, null to keep current).
    /// </summary>
    [StringLength(255, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 255 characters.")]
    public string? Name { get; set; }

    /// <summary>
    /// Updated notes (optional, null to keep current).
    /// </summary>
    [StringLength(2000, ErrorMessage = "Notes must not exceed 2000 characters.")]
    public string? Notes { get; set; }

    /// <summary>
    /// Enable or disable the certificate (optional, null to keep current).
    /// </summary>
    public bool? IsEnabled { get; set; }
}
