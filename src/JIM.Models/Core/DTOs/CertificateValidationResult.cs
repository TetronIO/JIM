namespace JIM.Models.Core.DTOs;

/// <summary>
/// Result of certificate validation.
/// </summary>
public class CertificateValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
