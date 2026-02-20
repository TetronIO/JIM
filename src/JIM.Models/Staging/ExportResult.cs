namespace JIM.Models.Staging;

/// <summary>
/// Represents the result of an export operation to a connected system.
/// Connectors return this to provide feedback about the export, including
/// any system-generated identifiers.
/// </summary>
public class ExportResult
{
    /// <summary>
    /// Whether the export operation succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the export failed. Should be human-readable.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The primary external ID assigned by the target system.
    /// For LDAP, this would be the objectGUID.
    /// For systems that don't generate IDs, this may be null.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// The secondary external ID from the target system.
    /// For LDAP, this would be the DN (which may differ from what was requested if the server normalised it).
    /// For systems without secondary IDs, this may be null.
    /// </summary>
    public string? SecondaryExternalId { get; set; }

    /// <summary>
    /// Creates a successful result with no external ID feedback.
    /// </summary>
    public static ExportResult Succeeded() => new() { Success = true };

    /// <summary>
    /// Creates a successful result with external ID feedback.
    /// </summary>
    public static ExportResult Succeeded(string? externalId, string? secondaryExternalId = null) =>
        new()
        {
            Success = true,
            ExternalId = externalId,
            SecondaryExternalId = secondaryExternalId
        };

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static ExportResult Failed(string errorMessage) =>
        new()
        {
            Success = false,
            ErrorMessage = errorMessage
        };
}
