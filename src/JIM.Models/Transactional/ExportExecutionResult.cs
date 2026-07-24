// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

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
    /// Total number of Pending Exports that were processed.
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
    /// Number of Pending Exports cancelled by pre-export reconciliation.
    /// CREATE+DELETE pairs and redundant UPDATE+DELETE pairs are detected and removed
    /// before export execution to avoid unnecessary round-trips to the Connected System.
    /// </summary>
    public int ReconciledCount { get; set; }

    /// <summary>
    /// IDs of the Pending Exports that were processed.
    /// Use these IDs to fetch the actual PendingExport records for detailed information.
    /// Note: These records may be deleted after successful export, use ProcessedExportItems instead.
    /// </summary>
    public List<Guid> ProcessedPendingExportIds { get; set; } = [];

    /// <summary>
    /// Information about each processed export for activity tracking.
    /// This is captured before Pending Exports are deleted, allowing execution item creation.
    /// </summary>
    public List<ProcessedExportItem> ProcessedExportItems { get; set; } = [];

    /// <summary>
    /// External IDs of containers that were created during this export session.
    /// Used by JIM to auto-select newly created containers in the hierarchy.
    /// </summary>
    public List<string> CreatedContainerExternalIds { get; set; } = [];

    #region Optimistic Export Apply (issue #1079)

    /// <summary>
    /// Number of successful, non-Delete Pending Exports whose exported attribute values were
    /// applied to their Connected System Object's in-memory attribute values.
    /// </summary>
    public int OptimisticApplyAppliedCount { get; set; }

    /// <summary>
    /// Number of Pending Exports skipped by optimistic apply because they were Delete-ChangeType
    /// (D6: the CSO obsolete/delete lifecycle owns that path).
    /// </summary>
    public int OptimisticApplySkippedCount { get; set; }

    /// <summary>
    /// Number of Pending Exports for which optimistic apply failed and was skipped (D7:
    /// failure-contained; the export itself already succeeded, and the confirming import
    /// self-heals). Never fails the batch, the Pending Export updates, or the Activity.
    /// </summary>
    public int OptimisticApplyFailedCount { get; set; }

    /// <summary>
    /// Number of Reference attribute values applied with <c>UnresolvedReferenceValue</c> populated
    /// but <c>ReferenceValueId</c> left null, because the referenced Connected System Object could
    /// not be resolved this run (D5). These rows still confirm and still diff clean on the
    /// confirming import.
    /// </summary>
    public int OptimisticApplyUnresolvedReferenceCount { get; set; }

    #endregion
}

/// <summary>
/// Information about a processed export, captured before deletion for activity tracking.
/// </summary>
public class ProcessedExportItem
{
    /// <summary>
    /// The change type that was exported (Create, Update, Delete).
    /// </summary>
    public PendingExportChangeType ChangeType { get; set; }

    /// <summary>
    /// The Connected System Object that was exported (if available).
    /// </summary>
    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    /// <summary>
    /// Number of attribute value changes in this export.
    /// </summary>
    public int AttributeChangeCount { get; set; }

    /// <summary>
    /// The attribute value changes from the Pending Export, captured before deletion.
    /// Used to create ConnectedSystemObjectChange records for export change history.
    /// </summary>
    public List<PendingExportAttributeValueChange> AttributeValueChanges { get; set; } = [];

    /// <summary>
    /// Whether the export succeeded.
    /// </summary>
    public bool Succeeded { get; set; }

    /// <summary>
    /// Error message if the export failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Number of retry attempts if the export failed.
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// Classifies the type of error when the export failed.
    /// Null when the export succeeded.
    /// </summary>
    public ConnectedSystemExportErrorType? ErrorType { get; set; }
}
