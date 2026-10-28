// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Staging;
using JIM.Models.Sync;
namespace JIM.Models.Transactional;

public class PendingExportAttributeValueChange
{
    /// <summary>
    /// Unique Value Generation (#242, Phase 2 work package H): set by <c>SyncEngine.ComputeAttributeValueChanges</c>
    /// when this change's target attribute is a generated export mapping's (<see cref="Logic.SyncRuleMapping.Generation"/>).
    /// The change's own value fields are left unset until the worker resolves this marker, immediately before
    /// the Pending Export is persisted, and clears it. Transient: never persisted (see
    /// <see cref="PendingGeneratedExportValue"/>'s own remarks).
    /// </summary>
    [NotMapped]
    public PendingGeneratedExportValue? PendingGeneration { get; set; }
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

    /// <summary>
    /// The export Synchronisation Rule whose mapping produced this value (stamped at staging time by
    /// SyncEngine.ComputeAttributeValueChanges and the deletion/class-membership staging paths in
    /// ExportEvaluationServer). Deliberately a soft pointer: no foreign key and no index, for the same reason
    /// as <see cref="ResolvedReferenceCsoId"/>: this table is written at bulk-export scale, and the id is never
    /// queried by value, only displayed alongside <see cref="SyncRuleName"/>. A dangling id (the rule deleted
    /// after staging) is expected and is not a data error; the name is what survives.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the contributing Synchronisation Rule's name at staging time. Denormalised so it survives
    /// deletion of the rule.
    /// </summary>
    public string? SyncRuleName { get; set; }

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

    /// <summary>
    /// The Connector wrote this attribute with an interim value, not the one this change asserts, because an Initial
    /// Password follows the Create (#2039): Active Directory refuses an enabled account with no password, so the LDAP
    /// Connector creates it disabled and names <c>userAccountControl</c> in
    /// <see cref="ConnectedSystemExportResult.AttributesAwaitingInitialPassword"/>, and the password's delivery
    /// enables it. Set by the export run from that report, while the change is
    /// <see cref="PendingExportAttributeChangeStatus.ExportedPendingConfirmation"/>.
    /// <para>
    /// A confirming import that finds the interim value holds the change (unchanged, not retried, nothing reported)
    /// while a password is still owed to the account, and releases it (back to
    /// <see cref="PendingExportAttributeChangeStatus.Pending"/>, the flag cleared) once none is, so the next export
    /// writes the asserted value as an ordinary Update. Every import judges it, whether or not it returns the account:
    /// one that does not (a Full Import skipping the unchanged account by its hash, a Delta Import with nothing to
    /// report) judges it after reconciliation, and releases it the same way. Drift Detection, in the run and in the preview,
    /// stages no correction restating such a change and carries it over when it replaces its Pending Export (#2501); export evaluation does not supersede it with an equal value, but
    /// withdraws it once the Metaverse now wants what the target already holds; and optimistic apply does not record
    /// its asserted value. False for every change no Connector reported, which makes all of that inert everywhere
    /// else; a partial index on the flag keeps the lookups free when no change carries it.
    /// </para>
    /// </summary>
    public bool AwaitsInitialPassword { get; set; }

    #endregion
}