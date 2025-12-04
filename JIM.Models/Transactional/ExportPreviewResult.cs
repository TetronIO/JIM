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

/// <summary>
/// Contains summary results of evaluating exports during sync preview (Q5 Decision).
///
/// When running in Preview Only mode, this object holds what changes
/// would be made without actually persisting or executing them.
/// </summary>
public class ExportEvaluationPreviewResult
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

/// <summary>
/// Result of an export execution run.
/// </summary>
public class ExportExecutionResult
{
    /// <summary>
    /// The Connected System ID this export was for.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The mode this export ran in (Preview Only or Preview and Sync).
    /// </summary>
    public SyncRunMode RunMode { get; set; }

    /// <summary>
    /// When the export execution started.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the export execution completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Total number of pending exports that were processed.
    /// </summary>
    public int TotalPendingExports { get; set; }

    /// <summary>
    /// Number of exports that succeeded.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of exports that failed.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Number of exports that were deferred due to unresolved references.
    /// </summary>
    public int DeferredCount { get; set; }

    /// <summary>
    /// Preview information for each pending export.
    /// Always populated regardless of run mode.
    /// </summary>
    public List<ExportPreviewResult> Previews { get; set; } = new();
}

/// <summary>
/// Options for controlling export execution behaviour.
/// </summary>
public class ExportExecutionOptions
{
    /// <summary>
    /// Number of exports to process in each batch.
    /// Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of parallel database operations.
    /// Default is 4.
    /// </summary>
    public int MaxParallelism { get; set; } = 4;
}

/// <summary>
/// Progress information for export execution.
/// </summary>
public class ExportProgressInfo
{
    /// <summary>
    /// Current phase of export execution.
    /// </summary>
    public ExportPhase Phase { get; set; }

    /// <summary>
    /// Total number of exports to process.
    /// </summary>
    public int TotalExports { get; set; }

    /// <summary>
    /// Number of exports processed so far.
    /// </summary>
    public int ProcessedExports { get; set; }

    /// <summary>
    /// Size of the current batch being processed.
    /// </summary>
    public int CurrentBatchSize { get; set; }

    /// <summary>
    /// Number of successful exports (only populated in Completed phase).
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed exports (only populated in Completed phase).
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Number of deferred exports (only populated in Completed phase).
    /// </summary>
    public int DeferredCount { get; set; }

    /// <summary>
    /// Human-readable progress message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int ProgressPercentage => TotalExports > 0
        ? (int)((double)ProcessedExports / TotalExports * 100)
        : 0;
}

/// <summary>
/// Phases of export execution.
/// </summary>
public enum ExportPhase
{
    /// <summary>
    /// Preparing exports (loading, generating previews).
    /// </summary>
    Preparing,

    /// <summary>
    /// Executing exports via connector.
    /// </summary>
    Executing,

    /// <summary>
    /// Resolving deferred references.
    /// </summary>
    ResolvingReferences,

    /// <summary>
    /// Export execution completed.
    /// </summary>
    Completed
}
