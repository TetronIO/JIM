// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
namespace JIM.Models.Transactional;

public class PendingExportAttributeValueChange
{
    /// <summary>
    /// The Id of the Connected System Object that this Reference attribute change's Distinguished
    /// Name was resolved to (<c>ExportExecutionServer.TryResolveReferencesFromLookup</c>), stamped
    /// at resolution time and persisted from then on. Used by optimistic export apply (issue #1079)
    /// to populate <see cref="JIM.Models.Staging.ConnectedSystemObjectAttributeValue.ReferenceValueId"/>
    /// without a further database round-trip, including across a worker restart or a cross-run retry
    /// (persisting this column is what makes that survive; see SPEC-1079B). Left null when the
    /// reference has never been resolved by this or an earlier export run.
    /// <para>
    /// Deliberately a soft pointer: no foreign key constraint and no index. The only consumer is the
    /// optimistic apply projection, which is never queried by value, so an index would tax the
    /// multi-million-row create-wave insert for no read benefit. A foreign key with cascading
    /// behaviour would also require touching every raw-SQL Connected System Object delete path
    /// (DB-side update plus tracked-instance fix-up; see src/CLAUDE.md "Raw SQL Writes Must Fix Up or
    /// Detach Tracked Instances"). A dangling id (the referenced Connected System Object deleted
    /// between resolution and a later retry) is contained downstream:
    /// <see cref="JIM.Models.Staging.ConnectedSystemObjectAttributeValue.ReferenceValueId"/> HAS a
    /// foreign key, so the apply's insert fails, is caught and counted by optimistic apply's failure
    /// containment, and the confirming import self-heals.
    /// </para>
    /// </summary>
    public Guid? ResolvedReferenceCsoId { get; set; }

    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the parent PendingExport. Explicit FK ensures the relationship is preserved
    /// when entities are loaded via AsNoTracking and re-attached with Entry().State = Modified
    /// (shadow FKs are lost in that scenario).
    /// </summary>
    public Guid? PendingExportId { get; set; }

    public ConnectedSystemObjectTypeAttribute Attribute { get; set; } = null!;
    public int AttributeId { get; set; }

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    public long? LongValue { get; set; }

    public decimal? DecimalValue { get; set; }

    public byte[]? ByteValue { get; set; }

    public Guid? GuidValue { get; set; }

    public bool? BoolValue { get; set; }

    /// <summary>
    /// Contains the unique identifier that the Connected System uses to refer to references in string form.
    /// </summary>
    public string? UnresolvedReferenceValue { get; set; }

    public PendingExportAttributeChangeType ChangeType { get; set; }

    #region Confirmation Tracking

    /// <summary>
    /// Current confirmation status of this attribute change.
    /// </summary>
    public PendingExportAttributeChangeStatus Status { get; set; } = PendingExportAttributeChangeStatus.Pending;

    /// <summary>
    /// How many times have we attempted to export this attribute change?
    /// Used to enforce retry limits and display in UI.
    /// </summary>
    public int ExportAttemptCount { get; set; }

    /// <summary>
    /// When was this attribute change last exported?
    /// </summary>
    public DateTime? LastExportedAt { get; set; }

    /// <summary>
    /// The value that was returned by the Connected System during confirming import
    /// when it didn't match our expected value. Useful for debugging mismatches.
    /// </summary>
    public string? LastImportedValue { get; set; }

    #endregion
}