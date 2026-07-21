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

            switch (change.ChangeType)
            {
                case PendingExportAttributeChangeType.Add:
                    ApplyAdd(cso, change, existing, resolvedReferenceCsoIdsByDn, delta);
                    break;

                case PendingExportAttributeChangeType.Update:
                    ApplyUpdate(cso, change, existing, resolvedReferenceCsoIdsByDn, delta);
                    break;

                case PendingExportAttributeChangeType.Remove:
                    ApplyRemove(change, existing, delta);
                    break;

                case PendingExportAttributeChangeType.RemoveAll:
                    ApplyRemoveAll(existing, delta);
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
    /// </summary>
    private static void ApplyAdd(
        ConnectedSystemObject cso,
        PendingExportAttributeValueChange change,
        List<ConnectedSystemObjectAttributeValue> existing,
        IReadOnlyDictionary<string, Guid> resolvedReferenceCsoIdsByDn,
        OptimisticExportApplyDelta delta)
    {
        if (SyncEngine.IsPendingChangeEmpty(change) || SyncEngine.ValueExistsOnCso(existing, change))
        {
            delta.SkippedChangeCount++;
            return;
        }

        var newValue = CreateAttributeValue(cso, change, resolvedReferenceCsoIdsByDn, delta);
        delta.Additions.Add(newValue);
        existing.Add(newValue);
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
        IReadOnlyDictionary<string, Guid> resolvedReferenceCsoIdsByDn,
        OptimisticExportApplyDelta delta)
    {
        if (SyncEngine.IsPendingChangeEmpty(change))
        {
            delta.SkippedChangeCount++;
            return;
        }

        if (existing.Count == 1 && SyncEngine.ValueExistsOnCso(existing, change))
        {
            delta.SkippedChangeCount++;
            return;
        }

        foreach (var oldValue in existing)
            delta.RemovalValueIds.Add(oldValue.Id);
        existing.Clear();

        var newValue = CreateAttributeValue(cso, change, resolvedReferenceCsoIdsByDn, delta);
        delta.Additions.Add(newValue);
        existing.Add(newValue);
    }

    /// <summary>
    /// Deletes the matching row(s) if present, mirroring <see cref="SyncEngine.ValueExistsOnCso"/>'s
    /// per-type comparison exactly (via <see cref="FindMatchingValues"/>) so a value is only ever
    /// removed under the same equality rule the confirming import's diff and reconciliation use;
    /// else a no-op.
    /// </summary>
    private static void ApplyRemove(
        PendingExportAttributeValueChange change,
        List<ConnectedSystemObjectAttributeValue> existing,
        OptimisticExportApplyDelta delta)
    {
        var matches = FindMatchingValues(existing, change);
        if (matches.Count == 0)
        {
            delta.SkippedChangeCount++;
            return;
        }

        foreach (var match in matches)
        {
            delta.RemovalValueIds.Add(match.Id);
            existing.Remove(match);
        }
    }

    /// <summary>
    /// Deletes every existing row for the attribute; a no-op when there is nothing to delete.
    /// </summary>
    private static void ApplyRemoveAll(List<ConnectedSystemObjectAttributeValue> existing, OptimisticExportApplyDelta delta)
    {
        if (existing.Count == 0)
        {
            delta.SkippedChangeCount++;
            return;
        }

        foreach (var value in existing)
            delta.RemovalValueIds.Add(value.Id);
        existing.Clear();
    }

    /// <summary>
    /// Finds the CSO attribute value row(s) matching a Pending Export attribute change's value,
    /// mirroring <see cref="SyncEngine.ValueExistsOnCso"/>'s per-type switch exactly (same
    /// comparisons, same Binary <c>SequenceEqual</c>, same Reference dual-field match) but returning
    /// the matching rows themselves rather than a boolean, since Remove needs to know which row(s)
    /// to delete.
    /// </summary>
    private static List<ConnectedSystemObjectAttributeValue> FindMatchingValues(
        List<ConnectedSystemObjectAttributeValue> csoValues,
        PendingExportAttributeValueChange change)
    {
        if (csoValues.Count == 0)
            return [];

        var attrType = change.Attribute?.Type ?? AttributeDataType.NotSet;

        return attrType switch
        {
            AttributeDataType.Text => !string.IsNullOrEmpty(change.StringValue)
                ? csoValues.Where(v => string.Equals(v.StringValue, change.StringValue, StringComparison.Ordinal)).ToList()
                : [],

            AttributeDataType.Number => change.IntValue.HasValue
                ? csoValues.Where(v => v.IntValue == change.IntValue).ToList()
                : [],

            AttributeDataType.LongNumber => change.LongValue.HasValue
                ? csoValues.Where(v => v.LongValue == change.LongValue).ToList()
                : [],

            AttributeDataType.DateTime => change.DateTimeValue.HasValue
                ? csoValues.Where(v => v.DateTimeValue == change.DateTimeValue).ToList()
                : [],

            AttributeDataType.Binary => change.ByteValue != null
                ? csoValues.Where(v => v.ByteValue != null && v.ByteValue.SequenceEqual(change.ByteValue)).ToList()
                : [],

            AttributeDataType.Boolean => change.BoolValue.HasValue
                ? csoValues.Where(v => v.BoolValue == change.BoolValue).ToList()
                : [],

            AttributeDataType.Guid => change.GuidValue.HasValue
                ? csoValues.Where(v => v.GuidValue == change.GuidValue).ToList()
                : [],

            AttributeDataType.Reference => FindMatchingReferenceValues(csoValues, change),

            _ => []
        };
    }

    private static List<ConnectedSystemObjectAttributeValue> FindMatchingReferenceValues(
        List<ConnectedSystemObjectAttributeValue> csoValues,
        PendingExportAttributeValueChange change)
    {
        if (!string.IsNullOrEmpty(change.UnresolvedReferenceValue))
        {
            var byUnresolved = csoValues
                .Where(v => string.Equals(v.UnresolvedReferenceValue, change.UnresolvedReferenceValue, StringComparison.Ordinal))
                .ToList();
            if (byUnresolved.Count > 0)
                return byUnresolved;
        }

        if (!string.IsNullOrEmpty(change.StringValue))
        {
            return csoValues
                .Where(v => string.Equals(v.UnresolvedReferenceValue, change.StringValue, StringComparison.Ordinal))
                .ToList();
        }

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
