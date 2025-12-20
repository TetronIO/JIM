namespace JIM.Models.Transactional;

/// <summary>
/// Preview information for a single attribute change within an export.
/// </summary>
public class ExportPreviewAttributeChange
{
    /// <summary>
    /// The ID of the target attribute in the connected system.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// The name of the target attribute.
    /// </summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// The type of change (Add, Update, Remove, RemoveAll).
    /// </summary>
    public PendingExportAttributeChangeType ChangeType { get; set; }

    /// <summary>
    /// String representation of the new value (for preview display).
    /// </summary>
    public string? NewValue { get; set; }
}
