namespace JIM.Models.Transactional;

/// <summary>
/// Contains the results of a sync preview operation (Q5 Decision).
///
/// When running in Preview Only mode, this object holds what changes
/// would be made without actually persisting or executing them.
/// </summary>
public class ExportPreviewResult
{
    /// <summary>
    /// The unique identifier of the pending export this preview is for.
    /// </summary>
    public Guid PendingExportId { get; set; }

    /// <summary>
    /// The type of change that will be made (Create, Update, Delete).
    /// </summary>
    public PendingExportChangeType ChangeType { get; set; }

    /// <summary>
    /// The ID of the Connected System Object that will be modified (if Update or Delete).
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The ID of the source Metaverse Object.
    /// </summary>
    public Guid? SourceMetaverseObjectId { get; set; }

    /// <summary>
    /// The attribute changes that will be made.
    /// </summary>
    public List<ExportPreviewAttributeChange> AttributeChanges { get; set; } = new();

    /// <summary>
    /// Summary of this export for display purposes.
    /// </summary>
    public string Summary => $"{ChangeType}: {AttributeChanges.Count} attribute change(s)";
}
