// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

/// <summary>
/// Pure, stateless calculator (issue #1079: optimistic export apply) that projects a batch's
/// successfully exported Pending Export attribute changes onto their Connected System Objects'
/// current in-memory attribute values. No I/O; the caller (<see cref="ExportExecutionServer"/>)
/// owns persistence and any database lookups the Reference fallback needs.
/// <para>
/// Re-running <see cref="CalculateDelta"/> for the same Pending Exports against a Connected System
/// Object graph already updated with a previous delta must yield an empty delta (idempotency, D3):
/// the calculator only ever reads the current in-memory state, and never mutates the Pending
/// Export's <see cref="PendingExportAttributeValueChange"/> instances.
/// </para>
/// </summary>
public static class OptimisticExportApplyCalculator
{
    /// <summary>
    /// Scans the batch for Reference attribute changes (Add/Update, non-empty payload) that need a
    /// database lookup to resolve their Distinguished Name to a Connected System Object Id: those
    /// whose <see cref="PendingExportAttributeValueChange.ResolvedReferenceCsoId"/> transient is
    /// unset (either resolved in an earlier export run, or never routed through
    /// <c>ExportExecutionServer.TryResolveReferencesFromLookup</c> at all because the Pending Export
    /// was never deferred). Remove/RemoveAll changes are excluded: they match by string, so they
    /// need no resolved Id. Deliberately over-inclusive rather than pre-computing whether the
    /// change will ultimately no-op (that requires the same CSO-state walk <see cref="CalculateDelta"/>
    /// already does): the wasted lookups are bounded by one export batch's worth of Distinguished
    /// Names, trivial next to the millions of rows this feature exists to avoid re-materialising.
    /// </summary>
    public static HashSet<string> CollectUnresolvedReferenceDns(IReadOnlyCollection<PendingExport> pendingExports)
    {
        var dns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pe in pendingExports)
        {
            if (pe.ChangeType == PendingExportChangeType.Delete)
                continue;

            foreach (var change in pe.AttributeValueChanges)
            {
                if (change.Attribute?.Type != AttributeDataType.Reference)
                    continue;

                if (change.ChangeType != PendingExportAttributeChangeType.Add &&
                    change.ChangeType != PendingExportAttributeChangeType.Update)
                    continue;

                if (change.ResolvedReferenceCsoId.HasValue)
                    continue;

                var dn = change.StringValue ?? change.UnresolvedReferenceValue;
                if (!string.IsNullOrEmpty(dn))
                    dns.Add(dn);
            }
        }

        return dns;
    }

    /// <summary>
    /// Calculates the additions/removals needed to bring each Pending Export's Connected System
    /// Object into line with what was just exported. Delete-ChangeType Pending Exports are skipped
    /// entirely (D6: the CSO obsolete/delete lifecycle owns that path); Pending Exports with no
    /// Connected System Object are skipped defensively (nothing to apply values to).
    /// </summary>
    /// <param name="pendingExports">The batch's successful Pending Exports, each with its current
    /// in-memory Connected System Object graph (<see cref="ConnectedSystemObject.AttributeValues"/>
    /// must reflect the state after any prior same-batch mutation, e.g.
    /// <c>BatchUpdateCsosAfterSuccessfulExportAsync</c>'s external Id additions).</param>
    /// <param name="resolvedReferenceCsoIdsByDn">Distinguished Name to Connected System Object Id
    /// map for Reference changes whose transient hint was unset, populated by the caller via
    /// <see cref="CollectUnresolvedReferenceDns"/> and a batched database lookup.</param>
    public static OptimisticExportApplyDelta CalculateDelta(
        IReadOnlyCollection<PendingExport> pendingExports,
        IReadOnlyDictionary<string, Guid> resolvedReferenceCsoIdsByDn)
    {
        var delta = new OptimisticExportApplyDelta();

        foreach (var pe in pendingExports)
        {
            if (pe.ChangeType == PendingExportChangeType.Delete)
                continue;

            var cso = pe.ConnectedSystemObject;
            if (cso == null)
                continue;

            ApplyPendingExport(cso, pe.AttributeValueChanges, resolvedReferenceCsoIdsByDn, delta);
        }

        return delta;
    }

    /// <summary>
    /// Applies one Pending Export's attribute changes against a per-attribute working copy of the
    /// CSO's current values, seeded from <see cref="ConnectedSystemObject.AttributeValues"/> and
    /// mutated locally as each change is processed (never touching the real collection: the caller
    /// applies the resulting delta after persistence, per D10). Seeding from the live collection,
    /// combined with running after <c>BatchUpdateCsosAfterSuccessfulExportAsync</c> (D11), is what
    /// gives the external-Id dedupe guarantee (D9).
    /// </summary>
    private static void ApplyPendingExport(
        ConnectedSystemObject cso,
        List<PendingExportAttributeValueChange> changes,
        IReadOnlyDictionary<string, Guid> resolvedReferenceCsoIdsByDn,
        OptimisticExportApplyDelta delta)
    {
        var workingByAttribute = cso.AttributeValues
            .GroupBy(av => av.AttributeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Per-attribute lookup index (#1079 perf fix), built lazily the first time a change touches
        // an attribute and maintained incrementally alongside workingByAttribute for the rest of
        // this Pending Export. See CalculatorAttributeIndex for why this exists.
        var indexByAttribute = new Dictionary<int, CalculatorAttributeIndex>();

        foreach (var change in changes)
        {
            // Orchestrator review finding #2: a change whose Attribute.Type is NotSet (or any
            // future type this calculator does not yet know how to map) must be skipped entirely,
            // for both additions and removals. Without this guard, IsPendingChangeEmpty only
            // inspects the typed value fields (not the Attribute's Type), so a change carrying a
            // payload but an unrecognised Type is not "empty"; CreateAttributeValue's switch then
            // has no matching arm and silently inserts a row with an Id, CSO and AttributeId but no
            // populated value - a junk row the next confirming import would just churn straight
            // back out.
            if (change.Attribute == null || !IsSupportedAttributeType(change.Attribute.Type))
            {
                delta.SkippedChangeCount++;
                continue;
            }

            if (!workingByAttribute.TryGetValue(change.AttributeId, out var existing))
            {
                existing = [];
                workingByAttribute[change.AttributeId] = existing;
            }

            if (!indexByAttribute.TryGetValue(change.AttributeId, out var index))
            {
                index = BuildAttributeIndex(change.Attribute.Type, existing);
                indexByAttribute[change.AttributeId] = index;
            }

            switch (change.ChangeType)
            {
                case PendingExportAttributeChangeType.Add:
                    ApplyAdd(cso, change, existing, index, resolvedReferenceCsoIdsByDn, delta);
                    break;

                case PendingExportAttributeChangeType.Update:
                    ApplyUpdate(cso, change, existing, index, resolvedReferenceCsoIdsByDn, delta);
                    break;

                case PendingExportAttributeChangeType.Remove:
                    ApplyRemove(change, existing, index, delta);
                    break;

                case PendingExportAttributeChangeType.RemoveAll:
                    ApplyRemoveAll(existing, index, delta);
                    break;

                default:
                    delta.SkippedChangeCount++;
                    break;
            }
        }
    }

    /// <summary>
    /// The eight attribute data types this calculator (and <see cref="SyncEngine.ValueExistsOnCso"/>,
    /// whose switch this mirrors) knows how to map onto a <see cref="ConnectedSystemObjectAttributeValue"/>
    /// column. <see cref="AttributeDataType.NotSet"/> and any future type not yet added here are
    /// deliberately excluded so callers skip rather than silently create an unpopulated row.
    /// </summary>
    private static bool IsSupportedAttributeType(AttributeDataType type) => type switch
    {
        AttributeDataType.Text => true,
        AttributeDataType.Number => true,
        AttributeDataType.LongNumber => true,
        AttributeDataType.DateTime => true,
        AttributeDataType.Binary => true,
        AttributeDataType.Boolean => true,
        AttributeDataType.Guid => true,
        AttributeDataType.Reference => true,
        _ => false
    };

    /// <summary>
    /// Add-if-absent (D4). Mirrors <see cref="SyncEngine.ValueExistsOnCso"/>'s empty-payload
    /// treatment (Add/Update only) implicitly: <see cref="SyncEngine.IsPendingChangeEmpty"/> is
    /// checked first, matching the reconciliation switch this calculator's semantics are drawn from.
    /// The existence probe goes through <paramref name="index"/> rather than
    /// <see cref="SyncEngine.ValueExistsOnCso"/> directly (#1079 perf fix: see
    /// <see cref="CalculatorAttributeIndex"/>) - equivalent for every data type, since
    /// ValueExistsOnCso's own per-type arms are exactly "hasValue &amp;&amp; csoValues.Any(match)".
    /// </summary>
    private static void ApplyAdd(
        ConnectedSystemObject cso,
        PendingExportAttributeValueChange change,
        List<ConnectedSystemObjectAttributeValue> existing,
        CalculatorAttributeIndex index,
        IReadOnlyDictionary<string, Guid> resolvedReferenceCsoIdsByDn,
        OptimisticExportApplyDelta delta)
    {
        if (SyncEngine.IsPendingChangeEmpty(change) || FindMatchesInIndex(index, existing, change).Count > 0)
        {
            delta.SkippedChangeCount++;
            return;
        }

        var newValue = CreateAttributeValue(cso, change, resolvedReferenceCsoIdsByDn, delta);
        delta.Additions.Add(newValue);
        existing.Add(newValue);
        AddToIndex(index, newValue);
    }

    /// <summary>
    /// Single-valued set semantics (D4): a complete no-op only when exactly one existing value
    /// matches the new one; otherwise every existing row for the attribute is replaced by the new
    /// value. An empty payload (clearing the attribute) is deliberately a no-op for apply purposes
    /// (mirrors the reconciliation empty-change case): the confirming import still reconciles the
    /// actual clear.
    /// </summary>
    private static void ApplyUpdate(
        ConnectedSystemObject cso,
        PendingExportAttributeValueChange change,
        List<ConnectedSystemObjectAttributeValue> existing,
        CalculatorAttributeIndex index,
        IReadOnlyDictionary<string, Guid> resolvedReferenceCsoIdsByDn,
        OptimisticExportApplyDelta delta)
    {
        if (SyncEngine.IsPendingChangeEmpty(change))
        {
            delta.SkippedChangeCount++;
            return;
        }

        if (existing.Count == 1 && FindMatchesInIndex(index, existing, change).Count > 0)
        {
            delta.SkippedChangeCount++;
            return;
        }

        foreach (var oldValue in existing)
            delta.RemovalValueIds.Add(oldValue.Id);
        existing.Clear();
        ClearIndex(index);

        var newValue = CreateAttributeValue(cso, change, resolvedReferenceCsoIdsByDn, delta);
        delta.Additions.Add(newValue);
        existing.Add(newValue);
        AddToIndex(index, newValue);
    }

    /// <summary>
    /// Deletes the matching row(s) if present, mirroring <see cref="SyncEngine.ValueExistsOnCso"/>'s
    /// per-type comparison exactly (via <see cref="FindMatchesInIndex"/>) so a value is only ever
    /// removed under the same equality rule the confirming import's diff and reconciliation use;
    /// else a no-op.
    /// </summary>
    private static void ApplyRemove(
        PendingExportAttributeValueChange change,
        List<ConnectedSystemObjectAttributeValue> existing,
        CalculatorAttributeIndex index,
        OptimisticExportApplyDelta delta)
    {
        var matches = FindMatchesInIndex(index, existing, change);
        if (matches.Count == 0)
        {
            delta.SkippedChangeCount++;
            return;
        }

        foreach (var match in matches)
        {
            delta.RemovalValueIds.Add(match.Id);
            existing.Remove(match);
            RemoveFromIndex(index, match);
        }
    }

    /// <summary>
    /// Deletes every existing row for the attribute; a no-op when there is nothing to delete.
    /// </summary>
    private static void ApplyRemoveAll(List<ConnectedSystemObjectAttributeValue> existing, CalculatorAttributeIndex index, OptimisticExportApplyDelta delta)
    {
        if (existing.Count == 0)
        {
            delta.SkippedChangeCount++;
            return;
        }

        foreach (var value in existing)
            delta.RemovalValueIds.Add(value.Id);
        existing.Clear();
        ClearIndex(index);
    }

    /// <summary>
    /// Per-attribute lookup index (issue #1079 perf fix). Full-scale validation (Scale500k25kGroups,
    /// 2026-07-21) measured 255 slow <c>OptimisticApply</c> instances totalling 77.5 minutes, all in
    /// the group-batch wave: <see cref="ApplyAdd"/> called <see cref="SyncEngine.ValueExistsOnCso"/>
    /// (a linear <c>List.Any()</c> scan) once per Add change, while every accepted Add appended to
    /// that same growing list, and <see cref="ApplyRemove"/>'s match-finding was another linear scan
    /// per change - making a Pending Export with M Add/Remove changes against an up-to-M-value
    /// attribute O(M^2). The live database has groups with up to 495,008 members: O(M^2) at that
    /// scale is ~2.4x10^11 comparisons for one group.
    /// <para>
    /// Built once per attribute from the seeded working list (<see cref="BuildAttributeIndex"/>) and
    /// maintained incrementally as changes are applied (<see cref="AddToIndex"/>,
    /// <see cref="RemoveFromIndex"/>, <see cref="ClearIndex"/>), so every existence/match probe
    /// (<see cref="FindMatchesInIndex"/>) is an O(1) average-case dictionary lookup instead of an
    /// O(n) scan. Unlike the reconciliation's #988 <c>AttributeValueIndex</c> (a bare
    /// <c>HashSet</c>, existence-only), this is keyed to
    /// <c>List&lt;ConnectedSystemObjectAttributeValue&gt;</c> per key: <see cref="ApplyRemove"/>
    /// needs to know WHICH row(s) matched, not just whether one did. Key derivation mirrors
    /// <see cref="SyncEngine.ValueExistsOnCso"/>'s switch exactly, per data type. Binary is not
    /// indexed: byte arrays do not hash usefully, and Binary attributes are never large
    /// multi-valued sets in practice (same carve-out and reasoning as the reconciliation's
    /// <c>AttributeValueIndex.BinaryValues</c>) - matches fall back to the original linear
    /// <c>SequenceEqual</c> scan over the working list.
    /// </para>
    /// </summary>
    private sealed class CalculatorAttributeIndex
    {
        public required AttributeDataType AttributeType { get; init; }
        public Dictionary<string, List<ConnectedSystemObjectAttributeValue>>? TextValues { get; init; }
        public Dictionary<string, List<ConnectedSystemObjectAttributeValue>>? ReferenceValues { get; init; }
        public Dictionary<int, List<ConnectedSystemObjectAttributeValue>>? NumberValues { get; init; }
        public Dictionary<long, List<ConnectedSystemObjectAttributeValue>>? LongNumberValues { get; init; }
        public Dictionary<Guid, List<ConnectedSystemObjectAttributeValue>>? GuidValues { get; init; }
        public Dictionary<bool, List<ConnectedSystemObjectAttributeValue>>? BooleanValues { get; init; }
        public Dictionary<long, List<ConnectedSystemObjectAttributeValue>>? DateTimeTicksValues { get; init; }
    }

    /// <summary>
    /// Builds a <see cref="CalculatorAttributeIndex"/> for one attribute's seeded working list.
    /// Key derivation mirrors <see cref="SyncEngine.ValueExistsOnCso"/>'s switch exactly, per data
    /// type: DateTime keys on <c>Ticks</c> (Kind-insensitive, exactly like the <c>==</c> comparison
    /// it replaces - mirrors the reconciliation's <c>DateTimeTicksValues</c> rationale); Reference
    /// keys on the CSO row's <see cref="ConnectedSystemObjectAttributeValue.UnresolvedReferenceValue"/>
    /// (both of a change's UnresolvedReferenceValue and StringValue probe against that one field -
    /// see <see cref="FindMatchesInIndex"/>).
    /// </summary>
    private static CalculatorAttributeIndex BuildAttributeIndex(AttributeDataType attrType, List<ConnectedSystemObjectAttributeValue> existing)
    {
        return attrType switch
        {
            AttributeDataType.Text => new CalculatorAttributeIndex
            {
                AttributeType = attrType,
                TextValues = existing
                    .Where(v => v.StringValue != null)
                    .GroupBy(v => v.StringValue!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal)
            },

            AttributeDataType.Reference => new CalculatorAttributeIndex
            {
                AttributeType = attrType,
                ReferenceValues = existing
                    .Where(v => v.UnresolvedReferenceValue != null)
                    .GroupBy(v => v.UnresolvedReferenceValue!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal)
            },

            AttributeDataType.Number => new CalculatorAttributeIndex
            {
                AttributeType = attrType,
                NumberValues = existing
                    .Where(v => v.IntValue.HasValue)
                    .GroupBy(v => v.IntValue!.Value)
                    .ToDictionary(g => g.Key, g => g.ToList())
            },

            AttributeDataType.LongNumber => new CalculatorAttributeIndex
            {
                AttributeType = attrType,
                LongNumberValues = existing
                    .Where(v => v.LongValue.HasValue)
                    .GroupBy(v => v.LongValue!.Value)
                    .ToDictionary(g => g.Key, g => g.ToList())
            },

            AttributeDataType.Guid => new CalculatorAttributeIndex
            {
                AttributeType = attrType,
                GuidValues = existing
                    .Where(v => v.GuidValue.HasValue)
                    .GroupBy(v => v.GuidValue!.Value)
                    .ToDictionary(g => g.Key, g => g.ToList())
            },

            AttributeDataType.Boolean => new CalculatorAttributeIndex
            {
                AttributeType = attrType,
                BooleanValues = existing
                    .Where(v => v.BoolValue.HasValue)
                    .GroupBy(v => v.BoolValue!.Value)
                    .ToDictionary(g => g.Key, g => g.ToList())
            },

            AttributeDataType.DateTime => new CalculatorAttributeIndex
            {
                AttributeType = attrType,
                DateTimeTicksValues = existing
                    .Where(v => v.DateTimeValue.HasValue)
                    .GroupBy(v => v.DateTimeValue!.Value.Ticks)
                    .ToDictionary(g => g.Key, g => g.ToList())
            },

            // Binary: no dictionary - FindMatchesInIndex falls back to a linear SequenceEqual scan.
            _ => new CalculatorAttributeIndex { AttributeType = attrType }
        };
    }

    /// <summary>
    /// Incrementally adds one newly-accepted value to its per-attribute index, keeping it in sync
    /// with the working list an Add/Update just appended to.
    /// </summary>
    private static void AddToIndex(CalculatorAttributeIndex index, ConnectedSystemObjectAttributeValue value)
    {
        switch (index.AttributeType)
        {
            case AttributeDataType.Text:
                if (value.StringValue != null) AddToKeyedIndex(index.TextValues!, value.StringValue, value);
                break;
            case AttributeDataType.Reference:
                if (value.UnresolvedReferenceValue != null) AddToKeyedIndex(index.ReferenceValues!, value.UnresolvedReferenceValue, value);
                break;
            case AttributeDataType.Number:
                if (value.IntValue.HasValue) AddToKeyedIndex(index.NumberValues!, value.IntValue.Value, value);
                break;
            case AttributeDataType.LongNumber:
                if (value.LongValue.HasValue) AddToKeyedIndex(index.LongNumberValues!, value.LongValue.Value, value);
                break;
            case AttributeDataType.Guid:
                if (value.GuidValue.HasValue) AddToKeyedIndex(index.GuidValues!, value.GuidValue.Value, value);
                break;
            case AttributeDataType.Boolean:
                if (value.BoolValue.HasValue) AddToKeyedIndex(index.BooleanValues!, value.BoolValue.Value, value);
                break;
            case AttributeDataType.DateTime:
                if (value.DateTimeValue.HasValue) AddToKeyedIndex(index.DateTimeTicksValues!, value.DateTimeValue.Value.Ticks, value);
                break;
            // Binary: no index to maintain - FindMatchesInIndex scans the working list directly.
        }
    }

    private static void AddToKeyedIndex<TKey>(
        Dictionary<TKey, List<ConnectedSystemObjectAttributeValue>> keyedIndex,
        TKey key,
        ConnectedSystemObjectAttributeValue value) where TKey : notnull
    {
        if (!keyedIndex.TryGetValue(key, out var list))
        {
            list = [];
            keyedIndex[key] = list;
        }
        list.Add(value);
    }

    /// <summary>
    /// Incrementally removes one deleted value from its per-attribute index, keeping it in sync
    /// with the working list a Remove just deleted from.
    /// </summary>
    private static void RemoveFromIndex(CalculatorAttributeIndex index, ConnectedSystemObjectAttributeValue value)
    {
        switch (index.AttributeType)
        {
            case AttributeDataType.Text:
                if (value.StringValue != null) RemoveFromKeyedIndex(index.TextValues!, value.StringValue, value);
                break;
            case AttributeDataType.Reference:
                if (value.UnresolvedReferenceValue != null) RemoveFromKeyedIndex(index.ReferenceValues!, value.UnresolvedReferenceValue, value);
                break;
            case AttributeDataType.Number:
                if (value.IntValue.HasValue) RemoveFromKeyedIndex(index.NumberValues!, value.IntValue.Value, value);
                break;
            case AttributeDataType.LongNumber:
                if (value.LongValue.HasValue) RemoveFromKeyedIndex(index.LongNumberValues!, value.LongValue.Value, value);
                break;
            case AttributeDataType.Guid:
                if (value.GuidValue.HasValue) RemoveFromKeyedIndex(index.GuidValues!, value.GuidValue.Value, value);
                break;
            case AttributeDataType.Boolean:
                if (value.BoolValue.HasValue) RemoveFromKeyedIndex(index.BooleanValues!, value.BoolValue.Value, value);
                break;
            case AttributeDataType.DateTime:
                if (value.DateTimeValue.HasValue) RemoveFromKeyedIndex(index.DateTimeTicksValues!, value.DateTimeValue.Value.Ticks, value);
                break;
            // Binary: no index to maintain.
        }
    }

    private static void RemoveFromKeyedIndex<TKey>(
        Dictionary<TKey, List<ConnectedSystemObjectAttributeValue>> keyedIndex,
        TKey key,
        ConnectedSystemObjectAttributeValue value) where TKey : notnull
    {
        if (keyedIndex.TryGetValue(key, out var list))
        {
            list.Remove(value);
            if (list.Count == 0)
                keyedIndex.Remove(key);
        }
    }

    /// <summary>
    /// Clears every entry from a per-attribute index, mirroring an <c>ApplyUpdate</c>/<c>ApplyRemoveAll</c>
    /// clearing the working list wholesale rather than removing one value at a time.
    /// </summary>
    private static void ClearIndex(CalculatorAttributeIndex index)
    {
        index.TextValues?.Clear();
        index.ReferenceValues?.Clear();
        index.NumberValues?.Clear();
        index.LongNumberValues?.Clear();
        index.GuidValues?.Clear();
        index.BooleanValues?.Clear();
        index.DateTimeTicksValues?.Clear();
    }

    /// <summary>
    /// Finds the CSO attribute value row(s) matching a Pending Export attribute change's value via
    /// the per-attribute index, mirroring <see cref="SyncEngine.ValueExistsOnCso"/>'s per-type
    /// switch exactly (same comparisons, same Binary <c>SequenceEqual</c>, same Reference dual-field
    /// match). Serves both existence checks (<see cref="ApplyAdd"/>/<see cref="ApplyUpdate"/>, via
    /// <c>.Count &gt; 0</c> - equivalent to <c>ValueExistsOnCso</c> for every data type, since its
    /// own per-type arms are exactly <c>hasValue &amp;&amp; csoValues.Any(match)</c>, the same
    /// predicate a dictionary lookup here applies) and match-finding
    /// (<see cref="ApplyRemove"/>, which needs to know which row(s) to delete). Always returns a
    /// fresh list, safe for the caller to mutate the underlying index/working list while iterating.
    /// </summary>
    private static List<ConnectedSystemObjectAttributeValue> FindMatchesInIndex(
        CalculatorAttributeIndex index,
        List<ConnectedSystemObjectAttributeValue> existing,
        PendingExportAttributeValueChange change)
    {
        switch (index.AttributeType)
        {
            case AttributeDataType.Text:
                if (string.IsNullOrEmpty(change.StringValue)) return [];
                return index.TextValues!.TryGetValue(change.StringValue, out var textMatches) ? textMatches.ToList() : [];

            case AttributeDataType.Number:
                if (!change.IntValue.HasValue) return [];
                return index.NumberValues!.TryGetValue(change.IntValue.Value, out var numberMatches) ? numberMatches.ToList() : [];

            case AttributeDataType.LongNumber:
                if (!change.LongValue.HasValue) return [];
                return index.LongNumberValues!.TryGetValue(change.LongValue.Value, out var longMatches) ? longMatches.ToList() : [];

            case AttributeDataType.Guid:
                if (!change.GuidValue.HasValue) return [];
                return index.GuidValues!.TryGetValue(change.GuidValue.Value, out var guidMatches) ? guidMatches.ToList() : [];

            case AttributeDataType.Boolean:
                if (!change.BoolValue.HasValue) return [];
                return index.BooleanValues!.TryGetValue(change.BoolValue.Value, out var boolMatches) ? boolMatches.ToList() : [];

            case AttributeDataType.DateTime:
                if (!change.DateTimeValue.HasValue) return [];
                return index.DateTimeTicksValues!.TryGetValue(change.DateTimeValue.Value.Ticks, out var dateTimeMatches) ? dateTimeMatches.ToList() : [];

            case AttributeDataType.Binary:
                // Byte arrays do not hash usefully and Binary attributes are never large
                // multi-valued sets in practice (mirrors the reconciliation's #988
                // AttributeValueIndex.BinaryValues carve-out) - linear scan against the actual
                // working list, exactly as the pre-#1079-perf-fix code did.
                return change.ByteValue != null
                    ? existing.Where(v => v.ByteValue != null && v.ByteValue.SequenceEqual(change.ByteValue)).ToList()
                    : [];

            case AttributeDataType.Reference:
                return FindMatchingReferenceValuesInIndex(index, change);

            default:
                return [];
        }
    }

    /// <summary>
    /// Mirrors the pre-#1079-perf-fix dual probe exactly: try <c>UnresolvedReferenceValue</c> first
    /// and only fall back to <c>StringValue</c> if that probe found nothing (not merely if it was
    /// empty). This is a deliberately different shape to <see cref="SyncEngine.ValueExistsOnCso"/>'s
    /// independent OR of the two probes, but always agrees with it on existence (true/false): for
    /// every combination of "probe has a non-empty value" x "probe finds a match", the two are
    /// equivalent - the OR is true exactly when this dual probe finds at least one row, since
    /// whichever probe finds nothing falls through here (or is skipped by the OR) while whichever
    /// finds something makes both true.
    /// </summary>
    private static List<ConnectedSystemObjectAttributeValue> FindMatchingReferenceValuesInIndex(
        CalculatorAttributeIndex index,
        PendingExportAttributeValueChange change)
    {
        if (!string.IsNullOrEmpty(change.UnresolvedReferenceValue) &&
            index.ReferenceValues!.TryGetValue(change.UnresolvedReferenceValue, out var byUnresolved) &&
            byUnresolved.Count > 0)
            return byUnresolved.ToList();

        if (!string.IsNullOrEmpty(change.StringValue))
            return index.ReferenceValues!.TryGetValue(change.StringValue, out var byString) ? byString.ToList() : [];

        return [];
    }

    /// <summary>
    /// Maps a Pending Export attribute change's typed payload onto a new
    /// <see cref="ConnectedSystemObjectAttributeValue"/> (data-type mapping mirrors
    /// <see cref="SyncEngine.ValueExistsOnCso"/>: Text-&gt;StringValue, Number-&gt;IntValue,
    /// LongNumber-&gt;LongValue, DateTime-&gt;DateTimeValue, Binary-&gt;ByteValue,
    /// Boolean-&gt;BoolValue, Guid-&gt;GuidValue, Reference-&gt;UnresolvedReferenceValue). The row's
    /// Id is pre-generated (raw SQL bulk insert bypasses EF's value generation).
    /// </summary>
    private static ConnectedSystemObjectAttributeValue CreateAttributeValue(
        ConnectedSystemObject cso,
        PendingExportAttributeValueChange change,
        IReadOnlyDictionary<string, Guid> resolvedReferenceCsoIdsByDn,
        OptimisticExportApplyDelta delta)
    {
        var value = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            AttributeId = change.AttributeId,
            Attribute = change.Attribute
        };

        switch (change.Attribute?.Type ?? AttributeDataType.NotSet)
        {
            case AttributeDataType.Text:
                value.StringValue = change.StringValue;
                break;

            case AttributeDataType.Number:
                value.IntValue = change.IntValue;
                break;

            case AttributeDataType.LongNumber:
                value.LongValue = change.LongValue;
                break;

            case AttributeDataType.DateTime:
                value.DateTimeValue = change.DateTimeValue;
                break;

            case AttributeDataType.Binary:
                value.ByteValue = change.ByteValue;
                break;

            case AttributeDataType.Boolean:
                value.BoolValue = change.BoolValue;
                break;

            case AttributeDataType.Guid:
                value.GuidValue = change.GuidValue;
                break;

            case AttributeDataType.Reference:
                PopulateReferenceValue(value, change, resolvedReferenceCsoIdsByDn, delta);
                break;
        }

        return value;
    }

    private static void PopulateReferenceValue(
        ConnectedSystemObjectAttributeValue value,
        PendingExportAttributeValueChange change,
        IReadOnlyDictionary<string, Guid> resolvedReferenceCsoIdsByDn,
        OptimisticExportApplyDelta delta)
    {
        // After export resolution the Distinguished Name lives in StringValue; fall back to
        // UnresolvedReferenceValue for a reference that was never routed through resolution
        // (D5).
        var dn = change.StringValue ?? change.UnresolvedReferenceValue;
        value.UnresolvedReferenceValue = dn;

        if (change.ResolvedReferenceCsoId.HasValue)
        {
            value.ReferenceValueId = change.ResolvedReferenceCsoId;
            return;
        }

        if (dn != null && resolvedReferenceCsoIdsByDn.TryGetValue(dn, out var resolvedId))
        {
            value.ReferenceValueId = resolvedId;
            return;
        }

        // Unresolvable this run: the row still confirms and still diffs clean (it matches on
        // UnresolvedReferenceValue), it merely stays unresolved until a future import touches it.
        value.ReferenceValueId = null;
        delta.UnresolvedReferenceCount++;
    }
}
