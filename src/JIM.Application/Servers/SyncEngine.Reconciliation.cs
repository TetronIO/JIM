// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
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
    public void ReconcileCsoAgainstPendingExport(
        ConnectedSystemObject connectedSystemObject,
        PendingExport? pendingExport,
        PendingExportReconciliationResult result)
    {
        if (pendingExport == null)
        {
            Log.Debug("ReconcileCsoAgainstPendingExport: No Pending Export for CSO {CsoId}", connectedSystemObject.Id);
            return;
        }

        // Only process exports that have been executed and are awaiting confirmation
        if (pendingExport.Status != PendingExportStatus.Exported &&
            pendingExport.Status != PendingExportStatus.ExportNotConfirmed)
        {
            Log.Debug("ReconcileCsoAgainstPendingExport: PendingExport {ExportId} status is {Status}, not awaiting confirmation. Skipping.",
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
        var attrIndexByAttrId = new Dictionary<int, AttributeValueIndex>(attrValuesByAttrId.Count);
        foreach (var (attributeId, values) in attrValuesByAttrId)
        {
            var attrType = values.Count > 0 ? values[0].Attribute?.Type ?? AttributeDataType.NotSet : AttributeDataType.NotSet;
            attrIndexByAttrId[attributeId] = BuildAttributeValueIndex(attrType, values);
        }

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

        // If this was a Create and the Secondary External ID was confirmed, transition to Update
        TransitionCreateToUpdateIfSecondaryExternalIdConfirmed(pendingExport, result);

        // Determine if the Pending Export should be deleted or updated
        var hasRemainingChanges = pendingExport.AttributeValueChanges.Any(ac =>
            ac.Status == PendingExportAttributeChangeStatus.Pending ||
            ac.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation ||
            ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed ||
            ac.Status == PendingExportAttributeChangeStatus.Failed);

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
    public static bool ValueExistsOnCso(List<ConnectedSystemObjectAttributeValue> csoValues, PendingExportAttributeValueChange attrChange)
    {
        if (csoValues.Count == 0)
            return false;

        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        return attrType switch
        {
            AttributeDataType.Text =>
                !string.IsNullOrEmpty(attrChange.StringValue) &&
                csoValues.Any(v => string.Equals(v.StringValue, attrChange.StringValue, StringComparison.Ordinal)),

            AttributeDataType.Number =>
                attrChange.IntValue.HasValue &&
                csoValues.Any(v => v.IntValue == attrChange.IntValue),

            AttributeDataType.LongNumber =>
                attrChange.LongValue.HasValue &&
                csoValues.Any(v => v.LongValue == attrChange.LongValue),

            AttributeDataType.DateTime =>
                attrChange.DateTimeValue.HasValue &&
                csoValues.Any(v => v.DateTimeValue == attrChange.DateTimeValue),

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
                // We need to check both the Pending Export value AND the CSO values to handle both cases
                (!string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue) &&
                 csoValues.Any(v => string.Equals(v.UnresolvedReferenceValue, attrChange.UnresolvedReferenceValue, StringComparison.Ordinal))) ||
                (!string.IsNullOrEmpty(attrChange.StringValue) &&
                 csoValues.Any(v => string.Equals(v.UnresolvedReferenceValue, attrChange.StringValue, StringComparison.Ordinal))),

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
    /// membership is exactly the same comparison the original code performed (e.g. DateTime equality
    /// via <c>Ticks</c>, which - like the <c>==</c> operator this replaces - ignores <see cref="DateTimeKind"/>;
    /// stringifying with a round-trip format would incorrectly make Kind significant). Binary values are
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
        public HashSet<int>? NumberValues { get; init; }
        public HashSet<long>? LongNumberValues { get; init; }
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
        return attrType switch
        {
            AttributeDataType.Text => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                TextValues = values.Where(v => v.StringValue != null).Select(v => v.StringValue!).ToHashSet(StringComparer.Ordinal)
            },

            AttributeDataType.Reference => new AttributeValueIndex
            {
                Count = values.Count,
                RawValues = values,
                // Both attrChange.UnresolvedReferenceValue and attrChange.StringValue are matched
                // against the CSO's UnresolvedReferenceValue (see ValueExistsOnCso's Reference case),
                // so a single index built from that one CSO-side field covers both probes.
                ReferenceValues = values.Where(v => v.UnresolvedReferenceValue != null).Select(v => v.UnresolvedReferenceValue!).ToHashSet(StringComparer.Ordinal)
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
                DateTimeTicksValues = values.Where(v => v.DateTimeValue.HasValue).Select(v => v.DateTimeValue!.Value.Ticks).ToHashSet()
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
        if (!typedSetAvailableForProbe)
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

            AttributeDataType.DateTime =>
                attrChange.DateTimeValue.HasValue &&
                (index.DateTimeTicksValues?.Contains(attrChange.DateTimeValue.Value.Ticks) ?? false),

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
                (!string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue) &&
                 (index.ReferenceValues?.Contains(attrChange.UnresolvedReferenceValue) ?? false)) ||
                (!string.IsNullOrEmpty(attrChange.StringValue) &&
                 (index.ReferenceValues?.Contains(attrChange.StringValue) ?? false)),

            _ => false
        };
    }

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
               !change.DateTimeValue.HasValue &&
               !change.BoolValue.HasValue &&
               !change.GuidValue.HasValue &&
               change.ByteValue == null &&
               change.UnresolvedReferenceValue == null;
    }

    /// <summary>
    /// Determines if an attribute change should be marked as Failed based on retry count.
    /// </summary>
    public static bool ShouldMarkAsFailed(PendingExportAttributeValueChange attrChange)
    {
        return attrChange.ExportAttemptCount >= DefaultMaxRetries;
    }

    /// <summary>
    /// If the Pending Export was a Create and the Secondary External ID attribute has been confirmed,
    /// transition it to an Update. Once an object is created, remaining unconfirmed attribute changes
    /// should be applied as updates. Connectors require the Secondary External ID (e.g., distinguishedName
    /// for LDAP) in the attribute changes for Create operations, but once confirmed, it is removed.
    /// Without this transition, retry attempts would fail because the connector cannot determine
    /// where to create the object.
    /// </summary>
    public static void TransitionCreateToUpdateIfSecondaryExternalIdConfirmed(PendingExport pendingExport, PendingExportReconciliationResult result)
    {
        if (pendingExport.ChangeType != PendingExportChangeType.Create)
            return;

        var secondaryExternalIdWasConfirmed = result.ConfirmedChanges.Any(ac =>
            ac.Attribute?.IsSecondaryExternalId == true);

        if (!secondaryExternalIdWasConfirmed)
            return;

        if (pendingExport.AttributeValueChanges.Count > 0)
        {
            var confirmedAttrName = result.ConfirmedChanges
                .FirstOrDefault(ac => ac.Attribute?.IsSecondaryExternalId == true)?.Attribute?.Name ?? "unknown";

            pendingExport.ChangeType = PendingExportChangeType.Update;
            Log.Debug("ReconcileCsoAgainstPendingExport: Transitioned Pending Export {ExportId} from Create to Update. " +
                "Secondary External ID attribute '{AttributeName}' was confirmed but {RemainingCount} attribute changes remain.",
                pendingExport.Id, confirmedAttrName, pendingExport.AttributeValueChanges.Count);
        }
    }

    /// <summary>
    /// Updates the PendingExport status based on its attribute change statuses.
    /// </summary>
    public static void UpdatePendingExportStatus(PendingExport pendingExport)
    {
        var allFailed = pendingExport.AttributeValueChanges.All(ac => ac.Status == PendingExportAttributeChangeStatus.Failed);
        var anyFailed = pendingExport.AttributeValueChanges.Any(ac => ac.Status == PendingExportAttributeChangeStatus.Failed);
        var anyPendingOrRetry = pendingExport.AttributeValueChanges.Any(ac =>
            ac.Status == PendingExportAttributeChangeStatus.Pending ||
            ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed);

        if (allFailed)
        {
            pendingExport.Status = PendingExportStatus.Failed;
        }
        else if (anyPendingOrRetry)
        {
            pendingExport.Status = PendingExportStatus.ExportNotConfirmed;
        }
        else if (anyFailed)
        {
            pendingExport.Status = PendingExportStatus.Exported;
        }
    }

    /// <summary>
    /// Gets the imported value as a string for debugging purposes.
    /// </summary>
    private static string? GetImportedValueAsString(Dictionary<int, List<ConnectedSystemObjectAttributeValue>> attrValuesByAttrId, PendingExportAttributeValueChange attrChange)
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
            AttributeDataType.DateTime => csoAttrValues.Select(v => v.DateTimeValue?.ToString("O")).Where(v => v != null),
            AttributeDataType.Boolean => csoAttrValues.Select(v => v.BoolValue?.ToString()).Where(v => v != null),
            AttributeDataType.Guid => csoAttrValues.Select(v => v.GuidValue?.ToString()).Where(v => v != null),
            AttributeDataType.Reference => csoAttrValues.Select(v => v.UnresolvedReferenceValue).Where(v => v != null),
            _ => Enumerable.Empty<string?>()
        };

        var valueList = values.ToList();
        return valueList.Count > 0 ? string.Join(", ", valueList) : "(no matching type values)";
    }

    /// <summary>
    /// Gets the expected (exported) value as a string for debugging purposes.
    /// </summary>
    private static string? GetExpectedValueAsString(PendingExportAttributeValueChange attrChange)
    {
        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        return attrType switch
        {
            AttributeDataType.Text => attrChange.StringValue ?? "(null)",
            AttributeDataType.Number => attrChange.IntValue?.ToString() ?? "(null)",
            AttributeDataType.LongNumber => attrChange.LongValue?.ToString() ?? "(null)",
            AttributeDataType.DateTime => attrChange.DateTimeValue?.ToString("O") ?? "(null)",
            AttributeDataType.Boolean => attrChange.BoolValue?.ToString() ?? "(null)",
            AttributeDataType.Guid => attrChange.GuidValue?.ToString() ?? "(null)",
            AttributeDataType.Reference => attrChange.UnresolvedReferenceValue ?? "(null)",
            AttributeDataType.Binary => attrChange.ByteValue != null ? $"(binary, {attrChange.ByteValue.Length} bytes)" : "(null)",
            _ => "(unknown type)"
        };
    }
}
