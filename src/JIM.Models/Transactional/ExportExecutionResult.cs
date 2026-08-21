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
    /// Number of exports that were deferred due to unresolved references. Counts exports that wrote
    /// nothing this run; an export that wrote what it could and is waiting only on its references is
    /// counted in <see cref="SuccessCount"/> and <see cref="PartiallyExportedCount"/> instead.
    /// </summary>
    public int DeferredCount { get; set; }

    /// <summary>
    /// Number of exports written in part (issue #1398): every change that could be written was, and
    /// the export stays pending for the reference changes that could not be resolved yet. Also
    /// counted in <see cref="SuccessCount"/>, because something was written.
    /// </summary>
    public int PartiallyExportedCount { get; set; }

    /// <summary>
    /// Number of reference values left unwritten because the referenced Metaverse Object has no
    /// Connected System Object in the target at all (issue #1398). A reference whose target exists
    /// but has no anchor yet is merely waiting and is not counted here. Reported per the Connected
    /// System's <see cref="ConnectedSystem.UnresolvedReferenceHandling"/>.
    /// </summary>
    public int UnresolvableReferenceCount { get; set; }

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

    #region Initial Password Provisioning (issue #1121)

    /// <summary>
    /// Number of newly provisioned accounts recorded as owed an initial password. The password itself is
    /// delivered by a later pass; this counts the work staged, not the passwords set.
    /// </summary>
    public int InitialPasswordsStagedCount { get; set; }

    /// <summary>
    /// Number of newly provisioned accounts JIM failed to record as owed an initial password.
    /// <para>
    /// Contained, but never silent. The accounts were created in the Connected System and their exports
    /// succeeded, so this must not fail the batch; unlike the optimistic apply above, though, nothing
    /// self-heals a password that nobody knows is owed, so the count is reported on the Activity and an
    /// administrator can re-stage by exporting again.
    /// </para>
    /// </summary>
    public int InitialPasswordStagingFailedCount { get; set; }

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

    /// <summary>
    /// The Pending Export this item reports on, captured before it is deleted. Set for every item raised by
    /// execution so a deferred item (one that wrote nothing) can still be tied to its export, and identifies
    /// the export cycle on the causal edge recording why the export happened (#1223).
    /// </summary>
    public Guid? PendingExportId { get; set; }

    /// <summary>
    /// The Metaverse Object whose change produced the Pending Export, copied from
    /// <see cref="PendingExport.SourceMetaverseObjectId"/>.
    /// </summary>
    public Guid? SourceMetaverseObjectId { get; set; }

    /// <summary>
    /// The Run Profile Execution Item of the synchronisation that staged the Pending Export, copied from
    /// <see cref="PendingExport.QueuedByRunProfileExecutionItemId"/>. Null for an export staged before that
    /// was recorded, or by a path that had no execution item to name.
    /// </summary>
    public Guid? QueuedByRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose provisioning decision produced this export, copied from
    /// <see cref="PendingExport.ProvisioningSyncRuleId"/>. Only ever set for a create.
    /// </summary>
    public int? ProvisioningSyncRuleId { get; set; }

    /// <summary>
    /// Copies the identifiers that say why this export happened off the Pending Export being carried out, and
    /// returns this item so it can be captured in a single expression at each call site.
    /// </summary>
    /// <remarks>
    /// A method rather than three assignments repeated per site: the export path builds these items in eight
    /// places, and a set of provenance fields that has to be remembered eight times is a set that will be
    /// forgotten in the ninth. The Pending Export row is deleted the moment the export succeeds, so a field
    /// missed here cannot be recovered afterwards.
    /// </remarks>
    public ProcessedExportItem WithCauseFrom(PendingExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        PendingExportId = export.Id;
        SourceMetaverseObjectId = export.SourceMetaverseObjectId;
        QueuedByRunProfileExecutionItemId = export.QueuedByRunProfileExecutionItemId;
        ProvisioningSyncRuleId = export.ProvisioningSyncRuleId;
        return this;
    }

    /// <summary>
    /// True when nothing was written to the Connected System for this export this run: it was deferred
    /// whole and this item exists only to carry <see cref="UnresolvedReferenceMessage"/>. Such an item
    /// is neither a success nor a failure of the export.
    /// </summary>
    public bool Deferred { get; set; }

    /// <summary>
    /// Set when the export left references unwritten that can never resolve as things stand: the
    /// referenced Metaverse Object has no Connected System Object in the target (issue #1398). Names
    /// the attribute and the referenced object. Only populated when the Connected System's Unresolved
    /// Reference Handling is Error; the processor records it on the Run Profile Execution Item as an
    /// unresolved reference error, exactly as the import side reports its own.
    /// </summary>
    public string? UnresolvedReferenceMessage { get; set; }
}
