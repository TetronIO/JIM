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
    /// The list of exports that would be created if this sync were executed.
    /// These are not persisted in Preview Only mode.
    /// </summary>
    public List<PendingExport> ProposedExports { get; set; } = new();

    /// <summary>
    /// Number of objects that would be created in target systems.
    /// </summary>
    public int ObjectsToCreate => ProposedExports.Count(e => e.ChangeType == PendingExportChangeType.Create);

    /// <summary>
    /// Number of objects that would be updated in target systems.
    /// </summary>
    public int ObjectsToUpdate => ProposedExports.Count(e => e.ChangeType == PendingExportChangeType.Update);

    /// <summary>
    /// Number of objects that would be deleted in target systems.
    /// </summary>
    public int ObjectsToDelete => ProposedExports.Count(e => e.ChangeType == PendingExportChangeType.Delete);

    /// <summary>
    /// Total number of attribute changes across all proposed exports.
    /// </summary>
    public int TotalAttributeChanges => ProposedExports.Sum(e => e.AttributeValueChanges.Count);

    /// <summary>
    /// Warnings encountered during preview evaluation.
    /// These are non-fatal issues that the admin should be aware of.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Errors encountered during preview evaluation.
    /// These would prevent the sync from completing if executed.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Whether the preview completed without errors.
    /// </summary>
    public bool IsSuccess => Errors.Count == 0;

    /// <summary>
    /// Summary of the preview for display purposes.
    /// </summary>
    public string Summary => $"Preview: {ObjectsToCreate} creates, {ObjectsToUpdate} updates, {ObjectsToDelete} deletes, {TotalAttributeChanges} attribute changes";
}
