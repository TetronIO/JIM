namespace JIM.Models.Transactional;

/// <summary>
/// Lightweight projection of a PendingExport containing only the fields needed for
/// pre-export reconciliation. Avoids loading full entity graphs (CSO, AttributeValues,
/// Attribute definitions) which at 100K+ objects consumes hundreds of MB unnecessarily.
/// </summary>
public class PendingExportSummary
{
    public Guid Id { get; set; }
    public PendingExportChangeType ChangeType { get; set; }
    public PendingExportStatus Status { get; set; }
    public Guid? ConnectedSystemObjectId { get; set; }
    public Guid? SourceMetaverseObjectId { get; set; }
}
