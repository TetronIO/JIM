// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Pending Export reconciliation logic — pure, stateless methods for comparing
/// imported CSO attribute values against Pending Export assertions.
/// Consolidated from PendingExportReconciliationService to ensure both
/// the sync path and import path use identical, comprehensive attribute matching.
/// </summary>
public partial class SyncEngine
{
    /// <summary>
    /// Default maximum number of export attempts before marking an attribute change as Failed.
    /// </summary>
    public const int DefaultMaxRetries = 5;

    /// <summary>
    /// Reconciles a Connected System Object against a pre-loaded Pending Export.
    /// This method does NOT perform any database operations — the caller is responsible for persistence.
    /// </summary>
    /// <param name="connectedSystemObject">The CSO that was just imported/updated.</param>
    /// <param name="pendingExport">The pre-loaded Pending Export for this CSO (or null if none).</param>
    /// <param name="result">The result object to populate with reconciliation outcomes.</param>
    /// <param name="metaverseObjectIdsOwedPassword">The Metaverse Objects still owed a password on this Connected System,
    /// or null when the caller did not look; read only for a change written with an interim value until an Initial
    /// Password is delivered (see <see cref="HoldOrReleaseChangeAwaitingInitialPassword"/>).</param>
    public void ReconcileCsoAgainstPendingExport(
        ConnectedSystemObject connectedSystemObject,
        PendingExport? pendingExport,
        PendingExportReconciliationResult result,
        IReadOnlySet<Guid>? metaverseObjectIdsOwedPassword = null)
    {
        if (pendingExport == null)
        {
            Log.Debug("ReconcileCsoAgainstPendingExport: No Pending Export for CSO {CsoId}", connectedSystemObject.Id);
            return;
        }

        // Parked and Executing Pending Exports must never be touched by reconciliation. Parked is held
        // for an administrator's Unique Value Generation decision (the target rejected a generated value
        // and another Connected System already holds it) and Executing is currently under a connector's
        // control; in both cases the export's own attribute changes may still carry
        // ExportedPendingConfirmation/ExportedNotConfirmed status left over from before the export
        // entered that state. Without this guard those statuses fall through the general check below
        // and get reconciled anyway: a Parked export would be retried with the very value the target
        // already rejected, and an Executing export would be raced against the in-flight write.
        // A Pending Export can only be genuinely stuck in Executing if the worker died mid-export; that
        // is recovered separately at Worker startup (Worker.ExecuteAsync ->
        // ExportExecutionServer.RecoverStrandedExecutingPendingExportsAsync ->
        // ISyncRepository.RecoverStrandedExecutingPendingExportsAsync), not by reconciliation.
        if (pendingExport.Status is PendingExportStatus.Parked or PendingExportStatus.Executing)
        {
            Log.Debug("ReconcileCsoAgainstPendingExport: PendingExport {ExportId} status is {Status}; excluded from reconciliation.",
                pendingExport.Id, pendingExport.Status);
            return;
        }

        // A Failed Pending Export has already exhausted its retries and requires manual intervention,
        // so it is deliberately excluded from the retry-and-confirm pipeline below: re-entering that
        // pipeline would restart error accounting on an export an administrator must act on, and could
        // resurrect it into the export queue on a false partial match. The one thing reconciliation
        // still does for it: if every attribute change it asserts is now visible on the CSO (the
        // administrator fixed the target by hand, or reissued the value out of band), the Pending
        // Export is cleared, exactly as a fully confirmed Exported one would be. Anything less than
        // full confirmation leaves it completely untouched: no status change, no attribute mutation,
        // no ErrorCount or attempt accounting.
        if (pendingExport.Status == PendingExportStatus.Failed)
        {
            // The same per-attribute value index the general path below builds (#988), so a large Failed
            // group export is checked in linear time rather than rebuilding a lookup per change.
            var failedIndexByAttrId = BuildAttributeValueIndexByAttributeId(connectedSystemObject);
            if (pendingExport.AttributeValueChanges.Count > 0 &&
                pendingExport.AttributeValueChanges.All(ac => IsAttributeChangeConfirmedFast(failedIndexByAttrId, ac)))
            {
                Log.Information("ReconcileCsoAgainstPendingExport: All {Count} attribute change(s) on Failed Pending Export {PeId} " +
                    "are now confirmed on CSO {CsoId} (administrator intervention); clearing it.",
                    pendingExport.AttributeValueChanges.Count, pendingExport.Id, connectedSystemObject.Id);
                result.ConfirmedChanges.AddRange(pendingExport.AttributeValueChanges);
                // Mirrors the general confirmation path below, which always empties AttributeValueChanges
                // of everything it just confirmed; here that is all of them, since the export is deleted.
                pendingExport.AttributeValueChanges.Clear();
                result.PendingExportDeleted = true;
                result.PendingExportToDelete = pendingExport;
            }
            else
            {
                Log.Debug("ReconcileCsoAgainstPendingExport: Failed Pending Export {PeId} is not fully confirmed on CSO {CsoId}; " +
                    "leaving untouched pending manual intervention.", pendingExport.Id, connectedSystemObject.Id);
            }

            return;
        }

        // Only process exports that have been executed and are awaiting confirmation. An export
        // written in part (issue #1398: the row went, a reference is still owed) sits Pending while
        // it waits, but its written changes still need confirming, so a Pending export with changes
        // awaiting confirmation is reconciled too. A Pending export that has written nothing yet has
        // nothing to confirm and is left exactly as it is, status included.
        if (pendingExport.Status != PendingExportStatus.Exported &&
            pendingExport.Status != PendingExportStatus.ExportNotConfirmed &&
            !pendingExport.AttributeValueChanges.Any(ac =>
                ac.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation ||
                ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed))
        {
            Log.Debug("ReconcileCsoAgainstPendingExport: PendingExport {ExportId} status is {Status} with nothing awaiting confirmation. Skipping.",
                pendingExport.Id, pendingExport.Status);
            return;
        }

        Log.Debug("ReconcileCsoAgainstPendingExport: Found Pending Export {ExportId} with {Count} attribute changes for CSO {CsoId}",
            pendingExport.Id, pendingExport.AttributeValueChanges.Count, connectedSystemObject.Id);

        // Build a lookup of CSO attribute values by attribute ID once per CSO.
        // This avoids repeated O(n) LINQ scans of cso.AttributeValues for each pending attribute change.
        // At 100K CSOs × ~10 attribute changes each, this eliminates millions of collection scans.
        var attrValuesByAttrId = connectedSystemObject.AttributeValues
            .GroupBy(av => av.AttributeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build a per-attribute value index once per CSO (#988): a HashSet (or, for typed numeric/
        // temporal/identifier data, a typed set) of comparison-ready values per attribute, built once
        // from attrValuesByAttrId above. This turns the per-change membership test that ValueExistsOnCso
        // used to do with List.Any() (O(n) per change - O(n²) for a large multi-valued attribute with
        // many pending changes, e.g. a 200,000-member group) into an O(1) average-case set lookup.
        var attrIndexByAttrId = BuildAttributeValueIndexByAttributeId(attrValuesByAttrId);

        // Process each attribute change that is awaiting confirmation
        var changesAwaitingConfirmation = pendingExport.AttributeValueChanges
            .Where(ac => ac.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation ||
                         ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed)
            .ToList();

        foreach (var attrChange in changesAwaitingConfirmation)
        {
            var confirmed = IsAttributeChangeConfirmedFast(attrIndexByAttrId, attrChange);

            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
            {
                Log.Verbose("ReconcileCsoAgainstPendingExport: Comparing attribute {AttrName} (ChangeType: {ChangeType}) for CSO {CsoId}. " +
                    "Expected: '{ExpectedValue}', Found: '{ActualValue}', Confirmed: {Confirmed}",
                    attrChange.Attribute?.Name ?? "unknown",
                    attrChange.ChangeType,
                    connectedSystemObject.Id,
                    LogSanitiser.Sanitise(GetExpectedValueAsString(attrChange)),
                    LogSanitiser.Sanitise(GetImportedValueAsString(attrValuesByAttrId, attrChange)),
                    confirmed);
            }

            if (confirmed)
            {
                result.ConfirmedChanges.Add(attrChange);
                Log.Debug("ReconcileCsoAgainstPendingExport: Attribute change {AttrChangeId} (Attr: {AttrName}) confirmed",
                    attrChange.Id, attrChange.Attribute?.Name ?? "unknown");
            }
            else if (IsAwaitingInitialPassword(attrChange))
            {
                HoldOrReleaseChangeAwaitingInitialPassword(connectedSystemObject.Id, connectedSystemObject.MetaverseObjectId, attrChange,
                    metaverseObjectIdsOwedPassword, result);
            }
            else
            {
                attrChange.Status = ShouldMarkAsFailed(attrChange)
                    ? PendingExportAttributeChangeStatus.Failed
                    : PendingExportAttributeChangeStatus.ExportedNotConfirmed;

                attrChange.LastImportedValue = GetImportedValueAsString(attrValuesByAttrId, attrChange);
                var expectedValue = GetExpectedValueAsString(attrChange);

                if (attrChange.Status == PendingExportAttributeChangeStatus.Failed)
                {
                    result.FailedChanges.Add(attrChange);
                    Log.Warning("ReconcileCsoAgainstPendingExport: Attribute change {AttrChangeId} (Attr: {AttrName}) failed after {Attempts} attempts. " +
                        "Expected: '{ExpectedValue}', Actual: '{ImportedValue}'",
                        attrChange.Id, attrChange.Attribute?.Name ?? "unknown", attrChange.ExportAttemptCount,
                        LogSanitiser.Sanitise(expectedValue), LogSanitiser.Sanitise(attrChange.LastImportedValue));
                }
                else
                {
                    result.RetryChanges.Add(attrChange);
                    Log.Debug("ReconcileCsoAgainstPendingExport: Attribute change {AttrChangeId} (Attr: {AttrName}) not confirmed, will retry (attempt {Attempt}). " +
                        "Expected: '{ExpectedValue}', Actual: '{ImportedValue}'",
                        attrChange.Id, attrChange.Attribute?.Name ?? "unknown", attrChange.ExportAttemptCount,
                        LogSanitiser.Sanitise(expectedValue), LogSanitiser.Sanitise(attrChange.LastImportedValue));
                }
            }
        }

        // Remove confirmed changes from the Pending Export. A single RemoveAll against a HashSet of
        // the confirmed changes (#988) replaces the previous per-change List.Remove loop, which was
        // O(n) per removal (List.Remove does a linear search-and-shift) - O(n²) for a Pending Export
        // with many confirmed changes, e.g. a large group's confirming import.
        if (result.ConfirmedChanges.Count > 0)
        {
            var confirmedChangeIds = new HashSet<PendingExportAttributeValueChange>(result.ConfirmedChanges);
            pendingExport.AttributeValueChanges.RemoveAll(ac => confirmedChangeIds.Contains(ac));
        }

        // Determine if the Pending Export should be deleted or updated
        var hasRemainingChanges = pendingExport.AttributeValueChanges.Any(ac =>
            ac.Status == PendingExportAttributeChangeStatus.Pending ||
            ac.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation ||
            ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed ||
            ac.Status == PendingExportAttributeChangeStatus.Failed);

        // Reconciliation only ever runs for a Connected System Object an import actually returned and
        // matched, so a Create Pending Export reaching this point already proves the object exists on
        // the target. Any changes still outstanding once confirmation has been processed must therefore
        // travel as an Update, never (re-)sent shaped as a Create the connector would reject.
        TransitionCreateToUpdateOnceObjectConfirmed(pendingExport, hasRemainingChanges);

        if (!hasRemainingChanges)
        {
            result.PendingExportDeleted = true;
            result.PendingExportToDelete = pendingExport;
        }
        else
        {
            UpdatePendingExportStatus(pendingExport);
            result.PendingExportToUpdate = pendingExport;
        }
    }

    /// <summary>
    /// A change sent with an interim value because an Initial Password follows the Create (#2039), still awaiting the
    /// confirming import that judges it: the only kind of change <see cref="HoldOrReleaseChangeAwaitingInitialPassword"/>
    /// applies to. Every other change, including one that carries the flag but has since left
    /// <see cref="PendingExportAttributeChangeStatus.ExportedPendingConfirmation"/>, is reconciled as it always was.
    /// </summary>
    public static bool IsAwaitingInitialPassword(PendingExportAttributeValueChange change) =>
        change.AwaitsInitialPassword && change.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation;

    /// <summary>
    /// Decides an unconfirmed change the Connector wrote with an interim value until an Initial Password is delivered
    /// (#2039): Active Directory was given the account disabled, and the password's delivery is what enables it, so the
    /// import seeing the interim value is expected rather than a failed write.
    /// <list type="bullet">
    /// <item><b>Held</b> while a password is still owed to the account (Pending, Delivering or Parked): left exactly as
    /// it is, neither retried (Active Directory refuses to enable an account with no password) nor reported as
    /// unconfirmed. The Pending Export keeps its status, so nothing is sent; the next import judges it again.</item>
    /// <item><b>Released</b> once none is (the password expired, was withdrawn or never staged, or the object has no
    /// Metaverse Object to owe one to): back to <see cref="PendingExportAttributeChangeStatus.Pending"/> with the flag
    /// cleared, so the next export writes the asserted value as an ordinary Update. Silent: the interim value was
    /// written on purpose. Should the target still refuse it, that export fails with its own actionable error.</item>
    /// </list>
    /// A caller that did not look up owed passwords (<paramref name="metaverseObjectIdsOwedPassword"/> null) gets a
    /// hold: the account stays disabled, which harms nothing, where a release would re-send a value the target refuses.
    /// <para>
    /// Judged at every import of the Connected System, whether or not the import returned the account: through
    /// <see cref="ReconcileCsoAgainstPendingExport"/> when it did, and through
    /// <see cref="JudgeChangesAwaitingInitialPasswordNotReconciled"/> when it did not.
    /// </para>
    /// </summary>
    private static void HoldOrReleaseChangeAwaitingInitialPassword(
        Guid connectedSystemObjectId,
        Guid? connectedSystemObjectMetaverseObjectId,
        PendingExportAttributeValueChange attrChange,
        IReadOnlySet<Guid>? metaverseObjectIdsOwedPassword,
        PendingExportReconciliationResult result)
    {
        var passwordOwed = connectedSystemObjectMetaverseObjectId is { } metaverseObjectId &&
            (metaverseObjectIdsOwedPassword == null || metaverseObjectIdsOwedPassword.Contains(metaverseObjectId));

        if (passwordOwed)
        {
            result.HeldChanges.Add(attrChange);
            Log.Debug("HoldOrReleaseChangeAwaitingInitialPassword: Attribute change {AttrChangeId} (Attr: {AttrName}) on CSO {CsoId} holds an interim value " +
                "until its Initial Password is delivered; held, not retried",
                attrChange.Id, attrChange.Attribute?.Name ?? "unknown", connectedSystemObjectId);
            return;
        }

        attrChange.Status = PendingExportAttributeChangeStatus.Pending;
        attrChange.AwaitsInitialPassword = false;
        result.ReleasedChanges.Add(attrChange);
        Log.Debug("HoldOrReleaseChangeAwaitingInitialPassword: Attribute change {AttrChangeId} (Attr: {AttrName}) on CSO {CsoId} held an interim value, " +
            "and no Initial Password is owed any more; released to be written as an ordinary Update",
            attrChange.Id, attrChange.Attribute?.Name ?? "unknown", connectedSystemObjectId);
    }

    /// <summary>
    /// Judges the changes a Pending Export holds until an Initial Password is delivered when the import that has just run
    /// did not return its Connected System Object, and so did not reconcile it (#2039). A Full Import skips an unchanged
    /// account by its content hash, and a Delta Import never returns one, so a hold whose password then expires or is
    /// cancelled (a change to the password, not the account), or is delivered without enabling the account, would
    /// otherwise never be judged again: the account would stay disabled, nothing would be reported, and Drift Detection
    /// would skip the object for ever.
    /// <para>
    /// Each held change is held or released exactly as <see cref="ReconcileCsoAgainstPendingExport"/> would
    /// (<see cref="HoldOrReleaseChangeAwaitingInitialPassword"/>). Nothing else is touched: the import did not read the
    /// account, so the values JIM holds for it may predate the export, and confirming or retrying any other change
    /// against them would be a guess. The same Pending Exports are excluded as there: Parked and Executing are never
    /// touched, and a Failed one awaits an administrator.
    /// </para>
    /// <para>
    /// After a release the Pending Export's status follows its changes as it does there, so the released change goes
    /// out on the next export, except for a Create no import has confirmed yet: confirming a Create is what turns it
    /// into an Update, and without that proof a Pending Create would be sent again to an object that may already exist.
    /// It keeps its status, and the released change goes out once an import confirms the Create.
    /// </para>
    /// </summary>
    /// <param name="connectedSystemObjectId">The Connected System Object the Pending Export writes to.</param>
    /// <param name="metaverseObjectId">The Metaverse Object it is joined to, or null when it is joined to none (and then
    /// nothing is owed).</param>
    /// <param name="pendingExport">The object's Pending Export, holding at least one such change.</param>
    /// <param name="result">Receives the held and released changes, and the Pending Export to persist when anything was
    /// released.</param>
    /// <param name="metaverseObjectIdsOwedPassword">The Metaverse Objects still owed a password on this Connected System.</param>
    public void JudgeChangesAwaitingInitialPasswordNotReconciled(
        Guid connectedSystemObjectId,
        Guid? metaverseObjectId,
        PendingExport pendingExport,
        PendingExportReconciliationResult result,
        IReadOnlySet<Guid> metaverseObjectIdsOwedPassword)
    {
        ArgumentNullException.ThrowIfNull(pendingExport);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(metaverseObjectIdsOwedPassword);

        if (pendingExport.Status is PendingExportStatus.Parked or PendingExportStatus.Executing or PendingExportStatus.Failed)
        {
            Log.Debug("JudgeChangesAwaitingInitialPasswordNotReconciled: Pending Export {ExportId} status is {Status}; its held change(s) are left as they are",
                pendingExport.Id, pendingExport.Status);
            return;
        }

        foreach (var heldChange in pendingExport.AttributeValueChanges.Where(IsAwaitingInitialPassword).ToList())
            HoldOrReleaseChangeAwaitingInitialPassword(connectedSystemObjectId, metaverseObjectId, heldChange, metaverseObjectIdsOwedPassword, result);

        if (result.ReleasedChanges.Count == 0)
            return;

        if (pendingExport.ChangeType != PendingExportChangeType.Create)
            UpdatePendingExportStatus(pendingExport);

        result.PendingExportToUpdate = pendingExport;
    }

    /// <summary>
    /// Determines if an attribute change has been confirmed. Convenience overload that builds
    /// a per-attribute lookup from the CSO's attribute values. For batch reconciliation of
    /// multiple attributes on the same CSO, use the dictionary-based overload to avoid
    /// repeated collection scans.
    /// </summary>
    public bool IsAttributeChangeConfirmed(ConnectedSystemObject cso, PendingExportAttributeValueChange attrChange)
    {
        var lookup = cso.AttributeValues
            .GroupBy(av => av.AttributeId)
            .ToDictionary(g => g.Key, g => g.ToList());
        return IsAttributeChangeConfirmed(lookup, attrChange);
    }

    /// <summary>
    /// Determines if an attribute change has been confirmed by comparing the exported value
    /// against a pre-built lookup of CSO attribute values. This avoids O(n) scans per attribute
    /// change when reconciling multiple changes for the same CSO.
    /// </summary>
    public bool IsAttributeChangeConfirmed(Dictionary<int, List<ConnectedSystemObjectAttributeValue>> attrValuesByAttrId, PendingExportAttributeValueChange attrChange)
    {
        if (attrChange.Attribute == null)
            return false;

        attrValuesByAttrId.TryGetValue(attrChange.AttributeId, out var csoAttrValues);
        csoAttrValues ??= new List<ConnectedSystemObjectAttributeValue>();

        switch (attrChange.ChangeType)
        {
            case PendingExportAttributeChangeType.Add:
            case PendingExportAttributeChangeType.Update:
                // For Add/Update with a null/empty value (clearing a single-valued attribute),
                // confirmation means the CSO should have no values for this attribute.
                if (IsPendingChangeEmpty(attrChange))
                    return csoAttrValues.Count == 0;

                // An Update against a multi-valued attribute sets its whole value set (#2182): it is confirmed only
                // when the attribute holds exactly that value, never when the value is merely among others.
                if (IsWholeSetUpdate(attrChange) && csoAttrValues.Count != 1)
                    return false;

                // For Add/Update with a real value, the value should exist on the CSO
                return ValueExistsOnCso(csoAttrValues, attrChange);

            case PendingExportAttributeChangeType.Remove:
                // For Remove, the value should NOT exist on the CSO
                return !ValueExistsOnCso(csoAttrValues, attrChange);

            case PendingExportAttributeChangeType.RemoveAll:
                // For RemoveAll, there should be no values for this attribute
                return csoAttrValues.Count == 0;

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks if the Pending Export attribute change value exists in the CSO's attribute values.
    /// Uses type-aware comparison based on the attribute's data type.
    /// </summary>
    /// <remarks>
    /// Text compares through <see cref="AttributeValueComparison"/> (#2051): an identifier (an External ID, a
    /// Secondary External ID) ignores case unless declared case-exact, because a directory reports a name in the case
    /// it stored, which need not be the case JIM wrote (#2195); every other text attribute compares exactly, so a change
    /// of case is confirmed only when the new case arrives. A resolved reference is the object it names, so it is
    /// confirmed by the referenced Connected System Object's id; its DN, like an unresolved one, ignores case.
    /// </remarks>
    public static bool ValueExistsOnCso(List<ConnectedSystemObjectAttributeValue> csoValues, PendingExportAttributeValueChange attrChange)
    {
        if (csoValues.Count == 0)
            return false;

        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        return attrType switch
        {
            AttributeDataType.Text =>
                !string.IsNullOrEmpty(attrChange.StringValue) &&
                csoValues.Any(v => string.Equals(v.StringValue, attrChange.StringValue, AttributeValueComparison.For(attrChange.Attribute))),

            AttributeDataType.Number =>
                attrChange.IntValue.HasValue &&
                csoValues.Any(v => v.IntValue == attrChange.IntValue),

            AttributeDataType.LongNumber =>
                attrChange.LongValue.HasValue &&
                csoValues.Any(v => v.LongValue == attrChange.LongValue),

            AttributeDataType.Decimal =>
                attrChange.DecimalValue.HasValue &&
                csoValues.Any(v => v.DecimalValue == attrChange.DecimalValue),

            AttributeDataType.DateTime =>
                attrChange.DateTimeValue.HasValue &&
                csoValues.Any(v => DateTimeAttributeValue.AreEqual(v.DateTimeValue, attrChange.DateTimeValue)),

            AttributeDataType.Binary =>
                attrChange.ByteValue != null &&
                csoValues.Any(v => v.ByteValue != null && v.ByteValue.SequenceEqual(attrChange.ByteValue)),

            AttributeDataType.Boolean =>
                attrChange.BoolValue.HasValue &&
                csoValues.Any(v => v.BoolValue == attrChange.BoolValue),

            AttributeDataType.Guid =>
                attrChange.GuidValue.HasValue &&
                csoValues.Any(v => v.GuidValue == attrChange.GuidValue),

            AttributeDataType.Reference =>
                // Reference attributes can be stored in two ways:
                // 1. UnresolvedReferenceValue — the raw DN string (before or after resolution)
                // 2. StringValue — set during export resolution (when UnresolvedReferenceValue is cleared)
                // We need to check both the Pending Export value AND the CSO values to handle both cases.
                // A resolved reference also matches by the referenced object's id, whatever either DN says.
                (attrChange.ResolvedReferenceCsoId.HasValue &&
                 csoValues.Any(v => v.ReferenceValueId == attrChange.ResolvedReferenceCsoId)) ||
                (!string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue) &&
                 csoValues.Any(v => string.Equals(v.UnresolvedReferenceValue, attrChange.UnresolvedReferenceValue, AttributeValueComparison.For(attrChange.Attribute)))) ||
                (!string.IsNullOrEmpty(attrChange.StringValue) &&
                 csoValues.Any(v => string.Equals(v.UnresolvedReferenceValue, attrChange.StringValue, AttributeValueComparison.For(attrChange.Attribute)))),

            _ => false
        };
    }

    /// <summary>
    /// Per-attribute value index (#988): a comparison-ready structure built once per CSO attribute
    /// so <see cref="IsAttributeChangeConfirmedFast"/> does O(1) average-case set lookups instead of
    /// the O(n) linear scans (<c>List.Any(...)</c>) that <see cref="ValueExistsOnCso"/> does. Exactly
    /// one of the typed sets is populated, matching the attribute's data type.
    /// </summary>
    /// <remarks>
    /// Number/LongNumber/Guid/Boolean/DateTime use typed sets (not stringified keys) so that set
    /// membership is exactly the same comparison <see cref="ValueExistsOnCso"/> performs (DateTime by its
    /// canonical key, <see cref="DateTimeAttributeValue.Key"/>, #2153, so neither Kind nor a sub-microsecond
    /// part is significant; stringifying with a round-trip format would make Kind significant). Binary values are
    /// not indexed: byte arrays do not hash usefully, and Binary attributes are never large multi-valued
    /// sets in practice (they are typically a single profile picture, certificate, etc.), so the original
    /// linear <c>SequenceEqual</c> scan is kept for them.
    /// </remarks>
    private sealed class AttributeValueIndex
    {
        public required int Count { get; init; }

        /// <summary>
        /// The raw CSO values this index was built from (a reference copy of the caller's list,
        /// no allocation cost). Kept on EVERY index so <see cref="ValueExistsInIndex"/> can fall
        /// back to the original <see cref="ValueExistsOnCso"/> linear comparison whenever the
        /// typed set a probe needs is missing; see the mismatch guard there for the scenario.
        /// </summary>
        public required List<ConnectedSystemObjectAttributeValue> RawValues { get; init; }

        public HashSet<string>? TextValues { get; init; }
        public HashSet<string>? ReferenceValues { get; init; }

        /// <summary>
        /// The ids of the Connected System Objects the values reference, so a resolved reference is matched by the
        /// object it names (see <see cref="ValueExistsOnCso"/>).
        /// </summary>
        public HashSet<Guid>? ReferenceIds { get; init; }

        /// <summary>
        /// How <see cref="TextValues"/> or <see cref="ReferenceValues"/> compares: taken from the attribute on the
        /// Connected System Object's values. A probe whose own attribute compares differently falls back to the linear
        /// comparison, so the two paths can never disagree.
        /// </summary>
        public StringComparison StringComparison { get; init; } = StringComparison.Ordinal;
        public HashSet<int>? NumberValues { get; init; }
        public HashSet<long>? LongNumberValues { get; init; }
        public HashSet<decimal>? DecimalValues { get; init; }
        public HashSet<Guid>? GuidValues { get; init; }
        public HashSet<bool>? BooleanValues { get; init; }
        public HashSet<long>? DateTimeTicksValues { get; init; }
        public List<ConnectedSystemObjectAttributeValue>? BinaryValues { get; init; }
    }

    /// <summary>
    /// Builds a <see cref="AttributeValueIndex"/> for one attribute's worth of CSO values (#988).
    /// Key derivation mirrors <see cref="ValueExistsOnCso"/>'s switch exactly, per data type.
    /// </summary>
    private static AttributeValueIndex BuildAttributeValueIndex(AttributeDataType attrType, List<ConnectedSystemObjectAttributeValue> values)
    {
        var attribute = values.Count > 0 ? values[0].Attribute : null;
        var stringComparison = AttributeValueComparison.For(attribute);
        var stringComparer = AttributeValueComparison.ComparerFor(attribute);

        return attrType switch
        {
            AttributeDataType.Text => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                StringComparison = stringComparison,
                TextValues = values.Where(v => v.StringValue != null).Select(v => v.StringValue!).ToHashSet(stringComparer)
            },

            AttributeDataType.Reference => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                // Both attrChange.UnresolvedReferenceValue and attrChange.StringValue are matched
                // against the CSO's UnresolvedReferenceValue (see ValueExistsOnCso's Reference case),
                // so a single index built from that one CSO-side field covers both probes.
                StringComparison = stringComparison,
                ReferenceValues = values.Where(v => v.UnresolvedReferenceValue != null).Select(v => v.UnresolvedReferenceValue!).ToHashSet(stringComparer),
                ReferenceIds = values.Where(v => v.ReferenceValueId.HasValue).Select(v => v.ReferenceValueId!.Value).ToHashSet()
            },

            AttributeDataType.Number => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                NumberValues = values.Where(v => v.IntValue.HasValue).Select(v => v.IntValue!.Value).ToHashSet()
            },

            AttributeDataType.LongNumber => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                LongNumberValues = values.Where(v => v.LongValue.HasValue).Select(v => v.LongValue!.Value).ToHashSet()
            },

            // Decimal hashing and equality are scale-insensitive (5.0m and 5.00m are one set member),
            // so set membership is numerically correct.
            AttributeDataType.Decimal => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                DecimalValues = values.Where(v => v.DecimalValue.HasValue).Select(v => v.DecimalValue!.Value).ToHashSet()
            },

            AttributeDataType.Guid => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                GuidValues = values.Where(v => v.GuidValue.HasValue).Select(v => v.GuidValue!.Value).ToHashSet()
            },

            AttributeDataType.Boolean => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                BooleanValues = values.Where(v => v.BoolValue.HasValue).Select(v => v.BoolValue!.Value).ToHashSet()
            },

            AttributeDataType.DateTime => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                DateTimeTicksValues = values.Where(v => v.DateTimeValue.HasValue).Select(v => DateTimeAttributeValue.Key(v.DateTimeValue!.Value)).ToHashSet()
            },

            // Binary: keep the existing linear SequenceEqual scan - byte arrays do not hash usefully,
            // and Binary attributes are never large multi-valued sets in practice, so this is fine.
            AttributeDataType.Binary => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                BinaryValues = values
            },

            _ => new AttributeValueIndex { Count = values.Count, RawValues = values }
        };
    }

    /// <summary>
    /// Set-based equivalent of <see cref="ValueExistsOnCso"/> (#988): checks whether the Pending Export
    /// attribute change's value exists in a pre-built <see cref="AttributeValueIndex"/>, mirroring
    /// <see cref="ValueExistsOnCso"/>'s switch exactly per data type but via O(1) set lookups.
    /// </summary>
    private static bool ValueExistsInIndex(AttributeValueIndex index, PendingExportAttributeValueChange attrChange)
    {
        if (index.Count == 0)
            return false;

        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        // Mismatch guard (belt and braces): the index's typed set is derived from the CSO values'
        // OWN Attribute navigation type, while this probe uses the CHANGE's attribute type. If the
        // value-side navigation was not loaded (Attribute null, so the index was built as NotSet
        // with no typed sets despite Count > 0), or the two types genuinely disagree, the set this
        // probe needs is null even though values exist. Silently probing a null set would report an
        // actually-confirmed change as unconfirmed (false retries, then false export confirmation
        // errors), so fall back to the original linear comparison, which switches on the change's
        // type and compares raw value fields regardless of the value-side navigation - guaranteeing
        // byte-identical behaviour with ValueExistsOnCso for every mismatch. The fast path below
        // stays hot for the normal case where the index and probe types agree.
        var typedSetAvailableForProbe = attrType switch
        {
            AttributeDataType.Text => index.TextValues != null,
            AttributeDataType.Number => index.NumberValues != null,
            AttributeDataType.LongNumber => index.LongNumberValues != null,
            AttributeDataType.Decimal => index.DecimalValues != null,
            AttributeDataType.DateTime => index.DateTimeTicksValues != null,
            AttributeDataType.Binary => index.BinaryValues != null,
            AttributeDataType.Boolean => index.BooleanValues != null,
            AttributeDataType.Guid => index.GuidValues != null,
            AttributeDataType.Reference => index.ReferenceValues != null,
            // NotSet and any future types: ValueExistsOnCso's default arm returns false, matching
            // the fast switch's own default arm, so either route is equivalent; take the fallback
            // for uniformity.
            _ => false
        };
        // The same guard for the comparison itself: a set built to ignore case cannot answer an exact probe, nor the
        // reverse, so a probe whose attribute compares differently from the values' attribute takes the linear path.
        if (!typedSetAvailableForProbe ||
            (attrType is AttributeDataType.Text or AttributeDataType.Reference &&
             index.StringComparison != AttributeValueComparison.For(attrChange.Attribute)))
            return ValueExistsOnCso(index.RawValues, attrChange);

        return attrType switch
        {
            AttributeDataType.Text =>
                !string.IsNullOrEmpty(attrChange.StringValue) &&
                (index.TextValues?.Contains(attrChange.StringValue) ?? false),

            AttributeDataType.Number =>
                attrChange.IntValue.HasValue &&
                (index.NumberValues?.Contains(attrChange.IntValue.Value) ?? false),

            AttributeDataType.LongNumber =>
                attrChange.LongValue.HasValue &&
                (index.LongNumberValues?.Contains(attrChange.LongValue.Value) ?? false),

            AttributeDataType.Decimal =>
                attrChange.DecimalValue.HasValue &&
                (index.DecimalValues?.Contains(attrChange.DecimalValue.Value) ?? false),

            AttributeDataType.DateTime =>
                attrChange.DateTimeValue.HasValue &&
                (index.DateTimeTicksValues?.Contains(DateTimeAttributeValue.Key(attrChange.DateTimeValue.Value)) ?? false),

            AttributeDataType.Binary =>
                attrChange.ByteValue != null &&
                (index.BinaryValues?.Any(v => v.ByteValue != null && v.ByteValue.SequenceEqual(attrChange.ByteValue)) ?? false),

            AttributeDataType.Boolean =>
                attrChange.BoolValue.HasValue &&
                (index.BooleanValues?.Contains(attrChange.BoolValue.Value) ?? false),

            AttributeDataType.Guid =>
                attrChange.GuidValue.HasValue &&
                (index.GuidValues?.Contains(attrChange.GuidValue.Value) ?? false),

            AttributeDataType.Reference =>
                (attrChange.ResolvedReferenceCsoId.HasValue &&
                 (index.ReferenceIds?.Contains(attrChange.ResolvedReferenceCsoId.Value) ?? false)) ||
                (!string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue) &&
                 (index.ReferenceValues?.Contains(attrChange.UnresolvedReferenceValue) ?? false)) ||
                (!string.IsNullOrEmpty(attrChange.StringValue) &&
                 (index.ReferenceValues?.Contains(attrChange.StringValue) ?? false)),

            _ => false
        };
    }

    /// <summary>
    /// Builds the per-attribute value index <see cref="IsAttributeChangeConfirmedFast"/> reads, from a
    /// Connected System Object's attribute values grouped by attribute id.
    /// </summary>
    private static Dictionary<int, AttributeValueIndex> BuildAttributeValueIndexByAttributeId(
        Dictionary<int, List<ConnectedSystemObjectAttributeValue>> attrValuesByAttrId)
    {
        var attrIndexByAttrId = new Dictionary<int, AttributeValueIndex>(attrValuesByAttrId.Count);
        foreach (var (attributeId, values) in attrValuesByAttrId)
        {
            var attrType = values.Count > 0 ? values[0].Attribute?.Type ?? AttributeDataType.NotSet : AttributeDataType.NotSet;
            attrIndexByAttrId[attributeId] = BuildAttributeValueIndex(attrType, values);
        }

        return attrIndexByAttrId;
    }

    /// <summary>
    /// Builds the per-attribute value index for a Connected System Object's attribute values.
    /// </summary>
    private static Dictionary<int, AttributeValueIndex> BuildAttributeValueIndexByAttributeId(ConnectedSystemObject connectedSystemObject)
        => BuildAttributeValueIndexByAttributeId(connectedSystemObject.AttributeValues
            .GroupBy(av => av.AttributeId)
            .ToDictionary(g => g.Key, g => g.ToList()));

    /// <summary>
    /// Set-based equivalent of <see cref="IsAttributeChangeConfirmed(Dictionary{int, List{ConnectedSystemObjectAttributeValue}}, PendingExportAttributeValueChange)"/>
    /// (#988): determines if an attribute change has been confirmed using a pre-built per-attribute
    /// value index instead of per-attribute value lists, so that <see cref="ReconcileCsoAgainstPendingExport"/>
    /// does O(1) average-case lookups per change instead of O(n) scans. Semantics are identical to the
    /// List-based overload; only the underlying comparison mechanism differs.
    /// </summary>
    private static bool IsAttributeChangeConfirmedFast(Dictionary<int, AttributeValueIndex> attrIndexByAttrId, PendingExportAttributeValueChange attrChange)
    {
        if (attrChange.Attribute == null)
            return false;

        attrIndexByAttrId.TryGetValue(attrChange.AttributeId, out var index);
        var count = index?.Count ?? 0;

        switch (attrChange.ChangeType)
        {
            case PendingExportAttributeChangeType.Add:
            case PendingExportAttributeChangeType.Update:
                // For Add/Update with a null/empty value (clearing a single-valued attribute),
                // confirmation means the CSO should have no values for this attribute.
                if (IsPendingChangeEmpty(attrChange))
                    return count == 0;

                // An Update against a multi-valued attribute is confirmed only when it holds exactly that value (#2182).
                if (IsWholeSetUpdate(attrChange) && count != 1)
                    return false;

                // For Add/Update with a real value, the value should exist on the CSO
                return index != null && ValueExistsInIndex(index, attrChange);

            case PendingExportAttributeChangeType.Remove:
                // For Remove, the value should NOT exist on the CSO
                return !(index != null && ValueExistsInIndex(index, attrChange));

            case PendingExportAttributeChangeType.RemoveAll:
                // For RemoveAll, there should be no values for this attribute
                return count == 0;

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks if a Pending Export attribute value change represents an empty/null value
    /// (i.e., clearing a single-valued attribute).
    /// </summary>
    public static bool IsPendingChangeEmpty(PendingExportAttributeValueChange change)
    {
        return change.StringValue == null &&
               !change.IntValue.HasValue &&
               !change.LongValue.HasValue &&
               !change.DecimalValue.HasValue &&
               !change.DateTimeValue.HasValue &&
               !change.BoolValue.HasValue &&
               !change.GuidValue.HasValue &&
               change.ByteValue == null &&
               change.UnresolvedReferenceValue == null;
    }

    /// <summary>
    /// Whether a change is an Update against a multi-valued attribute, which sets the attribute's whole value set to
    /// the one value it carries, or to nothing for an all-null Update (#2182). Export staging stages one where a
    /// single-valued Metaverse Attribute flows to a multi-valued attribute. Every Connector applies it as a replacement
    /// of the set, so no-net-change detection, export confirmation and optimistic apply all judge it done only when the
    /// attribute holds exactly that value.
    /// </summary>
    internal static bool IsWholeSetUpdate(PendingExportAttributeValueChange change) =>
        change.ChangeType == PendingExportAttributeChangeType.Update &&
        change.Attribute?.AttributePlurality == AttributePlurality.MultiValued;

    /// <summary>
    /// Determines if an attribute change should be marked as Failed based on retry count.
    /// </summary>
    public static bool ShouldMarkAsFailed(PendingExportAttributeValueChange attrChange)
    {
        return attrChange.ExportAttemptCount >= DefaultMaxRetries;
    }

    /// <summary>
    /// Transitions a Create Pending Export to an Update once reconciliation proves the object it targets
    /// already exists on the target. <see cref="ReconcileCsoAgainstPendingExport"/> is only ever invoked
    /// for a Connected System Object an import actually returned and matched, so reaching this method
    /// with a Create Pending Export already proves the object is there, regardless of which attribute(s)
    /// confirmed: any changes still outstanding once confirmation has been processed must therefore
    /// travel as a single Update, never (re-)sent shaped as a Create the connector would reject as
    /// creating an object that already exists.
    /// <para>
    /// This generalises, and subsumes, the two narrower triggers this method used to require
    /// individually: a confirmed Secondary External ID (connectors require it among a Create's own
    /// attribute changes to know where to create the object, but not for a subsequent Update), and every
    /// originally-exported change confirmed together with further changes queued
    /// <see cref="PendingExportAttributeChangeStatus.Pending"/> (appended while the Create awaited
    /// confirmation - see <c>ExportEvaluationServer</c>'s append-not-replace staging, which never deletes
    /// and replaces a Create that has already been sent). Both are now just examples of "changes remain
    /// after the object was confirmed to exist"; a single still-unconfirmed attribute change no longer
    /// blocks the transition the way it used to, because leaving it Create-shaped would re-send a Create
    /// for an object reconciliation has already proven exists.
    /// </para>
    /// </summary>
    /// <param name="hasRemainingChanges">Whether the Pending Export still has attribute changes after
    /// confirmed ones were removed (computed identically to, and immediately before, the delete/keep
    /// decision in <see cref="ReconcileCsoAgainstPendingExport"/>). When false the Pending Export is
    /// about to be deleted outright, so no transition is needed.</param>
    public static void TransitionCreateToUpdateOnceObjectConfirmed(PendingExport pendingExport, bool hasRemainingChanges)
    {
        if (pendingExport.ChangeType != PendingExportChangeType.Create)
            return;

        if (!hasRemainingChanges)
            return;

        pendingExport.ChangeType = PendingExportChangeType.Update;
        Log.Debug("ReconcileCsoAgainstPendingExport: Transitioned Pending Export {ExportId} from Create to Update: " +
            "the object was confirmed to exist on the target and {RemainingCount} attribute change(s) remain outstanding.",
            pendingExport.Id, pendingExport.AttributeValueChanges.Count);
    }

    /// <summary>
    /// Updates the PendingExport status based on its attribute change statuses.
    /// </summary>
    public static void UpdatePendingExportStatus(PendingExport pendingExport)
    {
        var allFailed = pendingExport.AttributeValueChanges.All(ac => ac.Status == PendingExportAttributeChangeStatus.Failed);
        var anyFailed = pendingExport.AttributeValueChanges.Any(ac => ac.Status == PendingExportAttributeChangeStatus.Failed);
        var anyRetry = pendingExport.AttributeValueChanges.Any(ac => ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed);
        var anyPending = pendingExport.AttributeValueChanges.Any(ac => ac.Status == PendingExportAttributeChangeStatus.Pending);

        if (allFailed)
        {
            pendingExport.Status = PendingExportStatus.Failed;
        }
        else if (anyRetry)
        {
            pendingExport.Status = PendingExportStatus.ExportNotConfirmed;
        }
        else if (anyPending)
        {
            // Nothing sent and unconfirmed; what remains has simply not been sent yet (a reference
            // still owed after a partial write, issue #1398, or a change merged in since the last
            // export). ExportNotConfirmed would claim a write that did not stick.
            pendingExport.Status = PendingExportStatus.Pending;
        }
        else if (anyFailed)
        {
            pendingExport.Status = PendingExportStatus.Exported;
        }
    }

    /// <summary>
    /// Gets the imported value as a string for debugging purposes.
    /// Internal for testing.
    /// </summary>
    internal static string? GetImportedValueAsString(Dictionary<int, List<ConnectedSystemObjectAttributeValue>> attrValuesByAttrId, PendingExportAttributeValueChange attrChange)
    {
        attrValuesByAttrId.TryGetValue(attrChange.AttributeId, out var csoAttrValues);
        csoAttrValues ??= new List<ConnectedSystemObjectAttributeValue>();

        if (csoAttrValues.Count == 0)
            return "(no values)";

        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        var values = attrType switch
        {
            AttributeDataType.Text => csoAttrValues.Select(v => v.StringValue).Where(v => v != null),
            AttributeDataType.Number => csoAttrValues.Select(v => v.IntValue?.ToString()).Where(v => v != null),
            AttributeDataType.LongNumber => csoAttrValues.Select(v => v.LongValue?.ToString()).Where(v => v != null),
            AttributeDataType.Decimal => csoAttrValues.Select(v => v.DecimalValue.HasValue ? DecimalAttributeValue.ToCanonicalString(v.DecimalValue.Value) : null).Where(v => v != null),
            AttributeDataType.DateTime => csoAttrValues.Select(v => v.DateTimeValue.HasValue ? DateTimeAttributeValue.ToRoundTripString(v.DateTimeValue.Value) : null).Where(v => v != null),
            AttributeDataType.Boolean => csoAttrValues.Select(v => v.BoolValue?.ToString()).Where(v => v != null),
            AttributeDataType.Guid => csoAttrValues.Select(v => v.GuidValue?.ToString()).Where(v => v != null),
            AttributeDataType.Reference => csoAttrValues.Select(v => v.UnresolvedReferenceValue).Where(v => v != null),
            AttributeDataType.Binary => csoAttrValues.Where(v => v.ByteValue != null).Select(v => $"(binary, {v.ByteValue!.Length} bytes)"),
            _ => Enumerable.Empty<string?>()
        };

        var valueList = values.ToList();
        return valueList.Count > 0 ? string.Join(", ", valueList) : "(no matching type values)";
    }

    /// <summary>
    /// Gets the expected (exported) value as a string for debugging purposes.
    /// Internal for testing.
    /// </summary>
    internal static string? GetExpectedValueAsString(PendingExportAttributeValueChange attrChange)
    {
        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        return attrType switch
        {
            AttributeDataType.Text => attrChange.StringValue ?? "(null)",
            AttributeDataType.Number => attrChange.IntValue?.ToString() ?? "(null)",
            AttributeDataType.LongNumber => attrChange.LongValue?.ToString() ?? "(null)",
            AttributeDataType.Decimal => attrChange.DecimalValue.HasValue ? DecimalAttributeValue.ToCanonicalString(attrChange.DecimalValue.Value) : "(null)",
            AttributeDataType.DateTime => attrChange.DateTimeValue.HasValue ? DateTimeAttributeValue.ToRoundTripString(attrChange.DateTimeValue.Value) : "(null)",
            AttributeDataType.Boolean => attrChange.BoolValue?.ToString() ?? "(null)",
            AttributeDataType.Guid => attrChange.GuidValue?.ToString() ?? "(null)",
            AttributeDataType.Reference => attrChange.UnresolvedReferenceValue ?? "(null)",
            AttributeDataType.Binary => attrChange.ByteValue != null ? $"(binary, {attrChange.ByteValue.Length} bytes)" : "(null)",
            _ => "(unknown type)"
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<DeferredDeleteReconciliation> ReconcileDeferredExportsAgainstPersistedDeletes(
        IReadOnlyCollection<PendingExport> deferredPendingExports,
        IReadOnlyDictionary<Guid, PendingExport> persistedPendingExportsByCsoId)
    {
        var pairs = new List<DeferredDeleteReconciliation>();

        foreach (var (csoId, persistedPe) in persistedPendingExportsByCsoId)
        {
            // Only a Delete that has not been attempted can be cancelled or relied on to supersede an Update.
            if (persistedPe.ChangeType != PendingExportChangeType.Delete || persistedPe.Status != PendingExportStatus.Pending)
                continue;

            var deferredPe = deferredPendingExports.FirstOrDefault(pe =>
                pe.ConnectedSystemObjectId == csoId &&
                pe.Status == PendingExportStatus.Pending);

            if (deferredPe == null)
                continue;

            if (deferredPe.ChangeType == PendingExportChangeType.Create)
                pairs.Add(new DeferredDeleteReconciliation(deferredPe, persistedPe, DeferredDeleteReconciliationOutcome.CancelBoth));
            else if (deferredPe.ChangeType == PendingExportChangeType.Update)
                pairs.Add(new DeferredDeleteReconciliation(deferredPe, persistedPe, DeferredDeleteReconciliationOutcome.DropDeferredUpdate));
        }

        return pairs;
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingExportAttributeValueChange> SelectQueuedChangesWithoutAuthority(
        PendingExport pendingExport,
        bool connectedSystemObjectIsJoined,
        IReadOnlyDictionary<int, SyncRule> syncRulesById,
        IReadOnlySet<int> classMembershipAttributeIds)
    {
        if (pendingExport.ChangeType != PendingExportChangeType.Update || pendingExport.Status == PendingExportStatus.Executing)
            return [];

        var queued = pendingExport.AttributeValueChanges
            .Where(c => c.SyncRuleId.HasValue &&
                        c.Status is PendingExportAttributeChangeStatus.Pending or PendingExportAttributeChangeStatus.ExportedNotConfirmed)
            .ToList();
        if (queued.Count == 0)
            return [];

        var withdrawn = new List<PendingExportAttributeValueChange>();
        var rulesLosingAttributeChanges = new HashSet<int>();

        foreach (var change in queued.Where(c => !classMembershipAttributeIds.Contains(c.AttributeId)))
        {
            var ruleId = change.SyncRuleId!.Value;
            if (!connectedSystemObjectIsJoined ||
                !syncRulesById.TryGetValue(ruleId, out var rule) ||
                !rule.Enabled ||
                !rule.AttributeFlowRules.Any(m => m.Enabled && MappingTargetAttributeId(m) == change.AttributeId))
            {
                withdrawn.Add(change);
                rulesLosingAttributeChanges.Add(ruleId);
            }
        }

        foreach (var change in queued.Where(c => classMembershipAttributeIds.Contains(c.AttributeId)))
        {
            var ruleId = change.SyncRuleId!.Value;
            if (!connectedSystemObjectIsJoined ||
                !syncRulesById.TryGetValue(ruleId, out var rule) ||
                !rule.Enabled ||
                rulesLosingAttributeChanges.Contains(ruleId))
            {
                withdrawn.Add(change);
            }
        }

        return withdrawn;
    }

    private static int? MappingTargetAttributeId(SyncRuleMapping mapping) =>
        mapping.TargetConnectedSystemAttributeId ?? mapping.TargetConnectedSystemAttribute?.Id;
}
