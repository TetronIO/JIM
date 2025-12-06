namespace JIM.Models.Core.DTOs;

/// <summary>
/// Request to update a certificate's editable properties.
/// </summary>
public class UpdateCertificateRequest
{
    /// <summary>
    /// New name for the certificate (optional, null to keep current).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Updated notes (optional, null to keep current).
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Enable or disable the certificate (optional, null to keep current).
    /// </summary>
    public bool? IsEnabled { get; set; }
}
