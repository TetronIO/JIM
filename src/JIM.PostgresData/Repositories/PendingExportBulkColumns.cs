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
        "ErrorCount", "MaxRetries", "LastAttemptedAt", "NextRetryAt",
        "LastErrorMessage", "LastErrorStackTrace", "SourceMetaverseObjectId",
        "HasUnresolvedReferences", "CreatedAt"
    ];

    /// <summary>
    /// Update columns for the retry/reconciliation bulk update (SyncRepository.CsOperations.cs):
    /// the status, retry and error fields mutated when Pending Exports are re-evaluated.
    /// </summary>
    internal static readonly string[] PendingExportsRetryUpdate =
    [
        "Status", "ChangeType", "ErrorCount", "MaxRetries", "LastAttemptedAt",
        "NextRetryAt", "LastErrorMessage", "LastErrorStackTrace", "HasUnresolvedReferences"
    ];

    /// <summary>
    /// Update columns for the export-result bulk update (ConnectedSystemRepository): as the retry
    /// update, minus ChangeType (fixed once evaluated on this path), plus ConnectedSystemObjectId
    /// (a provisioning export gains its CSO when the target object is created).
    /// </summary>
    internal static readonly string[] PendingExportsExportResultUpdate =
    [
        "Status", "ErrorCount", "MaxRetries", "LastAttemptedAt", "NextRetryAt",
        "LastErrorMessage", "LastErrorStackTrace", "HasUnresolvedReferences", "ConnectedSystemObjectId"
    ];

    /// <summary>
    /// Columns deliberately excluded from every Pending Export update list: the identity, source
    /// and creation timestamp are immutable once staged.
    /// </summary>
    internal static readonly string[] PendingExportsUpdateExclusions =
    [
        "Id", "ConnectedSystemId", "SourceMetaverseObjectId", "CreatedAt"
    ];

    /// <summary>
    /// Insert columns for the PendingExportAttributeValueChanges table.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChanges =
    [
        "Id", "PendingExportId", "AttributeId", "StringValue", "DateTimeValue",
        "IntValue", "LongValue", "DecimalValue", "ByteValue", "GuidValue", "BoolValue",
        "UnresolvedReferenceValue", "ChangeType", "Status", "ExportAttemptCount",
        "LastExportedAt", "LastImportedValue", "ResolvedReferenceCsoId"
    ];

    /// <summary>
    /// Update columns for the confirming-import bulk update (SyncRepository.CsOperations.cs):
    /// the export confirmation tracking fields.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesConfirmationUpdate =
    [
        "Status", "LastImportedValue", "ExportAttemptCount", "LastExportedAt"
    ];

    /// <summary>
    /// Update columns for the export-result bulk update (ConnectedSystemRepository): confirmation
    /// tracking plus the reference-resolution rewrites (a resolved reference moves from
    /// UnresolvedReferenceValue into StringValue before export, and the resolved target CSO is
    /// stamped onto ResolvedReferenceCsoId, #1079).
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesExportResultUpdate =
    [
        "Status", "ExportAttemptCount", "LastExportedAt", "StringValue",
        "UnresolvedReferenceValue", "LastImportedValue", "ResolvedReferenceCsoId"
    ];

    /// <summary>
    /// Columns deliberately excluded from every Pending Export attribute value change update list:
    /// the identity, target attribute, change classification and the staged non-string value
    /// carriers are immutable once staged.
    /// </summary>
    internal static readonly string[] PendingExportAttributeValueChangesUpdateExclusions =
    [
        "Id", "PendingExportId", "AttributeId", "ChangeType", "DateTimeValue",
        "IntValue", "LongValue", "DecimalValue", "ByteValue", "GuidValue", "BoolValue"
    ];
}
