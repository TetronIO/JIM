namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Result object for the pending export detail page. Contains the pending export
/// with capped MVA attribute changes and per-attribute total counts.
/// </summary>
public class PendingExportDetailResult
{
    public PendingExport PendingExport { get; set; } = null!;

    /// <summary>
    /// Per-attribute total change counts. Only populated when the detail page
    /// uses capped MVA loading. Key is the attribute name; value is the total
    /// count of changes in the database for that attribute.
    /// </summary>
    public Dictionary<string, int> AttributeChangeTotalCounts { get; set; } = new();
}
