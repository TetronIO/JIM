// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL paths that persist Pending
/// Exports and their attribute value changes (SyncRepository.CsOperations.cs and
/// ConnectedSystemRepository). Every writer MUST write values in exactly list order.
/// BulkInsertColumnCompletenessTests asserts the insert lists match the EF model's mapped columns
/// exactly, and that every column has a conscious home in one of the update lists or the documented
/// exclusion list, so a migration cannot silently leave a raw writer behind.
/// </summary>
internal static class PendingExportBulkColumns
{
    /// <summary>
    /// Insert columns for the PendingExports table.
    /// </summary>
    internal static readonly string[] PendingExports =
    [
        "Id", "ConnectedSystemId", "ConnectedSystemObjectId", "ChangeType", "Status",
        "ErrorCount", "FailingSince", "LastAttemptedAt",
        "LastErrorMessage", "LastErrorStackTrace", "SourceMetaverseObjectId",
        "HasUnresolvedReferences", "CreatedAt", "ProvisioningSyncRuleId",
        "QueuedByRunProfileExecutionItemId", "StagedByRunProfileExecutionItemIds"
    ];

    /// <summary>
    /// Update columns for the retry/reconciliation bulk update (SyncRepository.CsOperations.cs):
    /// the status and failure fields mutated when Pending Exports are re-evaluated.
    /// </summary>
    internal static readonly string[] PendingExportsRetryUpdate =
    [
        "Status", "ChangeType", "ErrorCount", "FailingSince", "LastAttemptedAt",
        "LastErrorMessage", "LastErrorStackTrace", "HasUnresolvedReferences"
    ];

    /// <summary>
    /// Update columns for the export-result bulk update (ConnectedSystemRepository): as the retry
    /// update, plus ConnectedSystemObjectId (a provisioning export gains its CSO when the target
    /// object is created). ChangeType is included because a Create written in part, without the
    /// reference changes it could not resolve, becomes an Update at execution time (issue #1398):
    /// the row now exists, so what is sent later must never be a second insert.
    /// </summary>
    internal static readonly string[] PendingExportsExportResultUpdate =
    [
        "Status", "ChangeType", "ErrorCount", "FailingSince", "LastAttemptedAt",
        "LastErrorMessage", "LastErrorStackTrace", "HasUnresolvedReferences", "ConnectedSystemObjectId"
    ];

    /// <summary>
    /// Columns deliberately excluded from every Pending Export update list: the identity, source
    /// and creation timestamp are immutable once staged. ProvisioningSyncRuleId joins them because
    /// it records which rule's provisioning decision produced this export, which is a fact about
    /// how it came to exist and cannot change afterwards, and QueuedByRunProfileExecutionItemId and
    /// StagedByRunProfileExecutionItemIds for exactly the same reason: they name the synchronisations that
    /// staged the export, which no retry or reconciliation can change. Only another staging run adds to
    /// them, through SetPendingExportQueueingItemsAsync's own targeted statement.
    /// </summary>
    internal static readonly string[] PendingExportsUpdateExclusions =
    [
        "Id", "ConnectedSystemId", "SourceMetaverseObjectId", "CreatedAt", "ProvisioningSyncRuleId",
        "QueuedByRunProfileExecutionItemId", "StagedByRunProfileExecutionItemIds"
    ];

    /// <summary>
    /// Insert columns for the PendingExportAttributeValueChanges table.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChanges =
    [
        "Id", "PendingExportId", "AttributeId", "StringValue", "DateTimeValue",
        "IntValue", "LongValue", "DecimalValue", "ByteValue", "GuidValue", "BoolValue",
        "UnresolvedReferenceValue", "ChangeType", "Status", "ExportAttemptCount",
        "LastExportedAt", "LastImportedValue", "ResolvedReferenceCsoId", "SyncRuleId", "SyncRuleName",
        "AwaitsInitialPassword", "AwaitsExchange"
    ];

    /// <summary>
    /// Update columns for the confirming-import bulk update (SyncRepository.CsOperations.cs):
    /// the export confirmation tracking fields. AwaitsInitialPassword is among them because the
    /// confirming import clears it when it releases a change held until an Initial Password is
    /// delivered (#2039). AwaitsExchange likewise: the confirming import clears it when it releases a
    /// connector-defined change whose object no longer has a change on the Exchange queue (#2021).
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesConfirmationUpdate =
    [
        "Status", "LastImportedValue", "ExportAttemptCount", "LastExportedAt", "AwaitsInitialPassword", "AwaitsExchange"
    ];

    /// <summary>
    /// Update columns for the export-result bulk update (ConnectedSystemRepository): confirmation
    /// tracking plus the reference-resolution rewrites (a resolved reference moves from
    /// UnresolvedReferenceValue into StringValue before export, and the resolved target CSO is
    /// stamped onto ResolvedReferenceCsoId, #1079). AwaitsInitialPassword is set here when the
    /// Connector reports it wrote the attribute with an interim value until an Initial Password is
    /// delivered (#2039), and AwaitsExchange when export staging hands a connector-defined change to the
    /// Exchange queue (#2021).
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesExportResultUpdate =
    [
        "Status", "ExportAttemptCount", "LastExportedAt", "StringValue",
        "UnresolvedReferenceValue", "LastImportedValue", "ResolvedReferenceCsoId", "AwaitsInitialPassword", "AwaitsExchange"
    ];

    /// <summary>
    /// The set-based statement of the export-result writer (ConnectedSystemRepository.UpdatePendingExportResultsAsync,
    /// #2851): the sent mark, written to exactly the changes a batch sent, by id. Status becomes awaiting confirmation,
    /// ExportAttemptCount is counted on from what is stored, and LastExportedAt is the batch's one sending time.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesExportResultSentUpdate =
    [
        "Status", "ExportAttemptCount", "LastExportedAt"
    ];

    /// <summary>
    /// The export-result writer's statement for the changes a batch revised besides marking them sent: the value, which a
    /// Connector may replace as it writes (the LDAP Connector records the default it writes for a protected attribute it
    /// cannot clear), and the Initial Password and Exchange holds the batch's results set (#2039, #2021). Written only for
    /// the changes whose value or holds differ from what they held when the batch was sent.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesExportResultRevisionUpdate =
    [
        "StringValue", "AwaitsInitialPassword", "AwaitsExchange"
    ];

    /// <summary>
    /// The columns of the full export-result update that the export-result writer never writes, because export never
    /// changes them once a batch is loaded: reference resolution is saved before the batch is sent
    /// (PersistResolvedReferencesAsync), so UnresolvedReferenceValue and ResolvedReferenceCsoId are as stored, and
    /// LastImportedValue is set only by the confirming import. With the sent and revision lists they make up
    /// PendingExportAttributeValueChangesExportResultUpdate exactly (BulkInsertColumnCompletenessTests), so a column that
    /// update gains has to be placed in this writer too.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesExportResultUnwritten =
    [
        "UnresolvedReferenceValue", "LastImportedValue", "ResolvedReferenceCsoId"
    ];

    /// <summary>
    /// Update columns for the deferred export pass's save of a chunk's reference resolutions
    /// (ConnectedSystemRepository.PersistResolvedReferencesAsync): only what resolution changes, the
    /// resolved value, the cleared unresolved marker and the resolved target CSO (#1079). Everything
    /// else on the change is written by the export-result update once the change has been sent.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesReferenceResolutionUpdate =
    [
        "StringValue", "UnresolvedReferenceValue", "ResolvedReferenceCsoId"
    ];

    /// <summary>
    /// Columns deliberately excluded from every Pending Export attribute value change update list:
    /// the identity, target attribute, change classification and the staged non-string value
    /// carriers are immutable once staged. SyncRuleId/SyncRuleName join them: they record which rule's
    /// mapping produced the value at staging time, a fact about how the change came to exist that no
    /// later retry or reconciliation can change.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesUpdateExclusions =
    [
        "Id", "PendingExportId", "AttributeId", "ChangeType", "DateTimeValue",
        "IntValue", "LongValue", "DecimalValue", "ByteValue", "GuidValue", "BoolValue",
        "SyncRuleId", "SyncRuleName"
    ];
}
