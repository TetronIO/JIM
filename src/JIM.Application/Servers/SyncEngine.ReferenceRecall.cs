// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

/// <summary>
/// Reference recall decisions (#288 plan Phase 1d), extracted from <see cref="ExportEvaluationServer"/>'s
/// recall fast path and fallback (#908/#1003): what removal change a matched target row synthesises, how
/// recall changes combine with a Pending Export already attached to the CSO, and the purge of changes whose
/// unresolved reference is a deleted object. The capture, existence queries and persistence stay with the
/// orchestrator.
/// </summary>
public partial class SyncEngine
{
    /// <summary>
    /// Decides the removal change a recall stages for one matched target row: a multi-valued source
    /// synthesises a Remove carrying the resolved value (the connector must be told which value to remove),
    /// a single-valued source synthesises a null-clearing Update (the same shape full evaluation produces),
    /// and a multi-valued removal with no resolvable value stages nothing, because a Remove that names no
    /// value cannot be exported. The orchestrator counts a null as a dropped change.
    /// </summary>
    /// <param name="flow">The direct reference flow whose target attribute still holds the deleted value.</param>
    /// <param name="resolvedRemovalValue">The deleted object's resolved value in the flow's target system
    /// (for example its Distinguished Name), captured before deletion; null when it was never captured.</param>
    public PendingExportAttributeValueChange? DecideRecallRemovalChange(
        ReferenceRecallDirectFlow flow,
        string? resolvedRemovalValue)
    {
        if (flow.SourcePlurality == AttributePlurality.MultiValued)
        {
            if (resolvedRemovalValue == null)
                return null;

            return new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                Attribute = flow.TargetAttribute,
                AttributeId = flow.TargetAttribute.Id,
                ChangeType = PendingExportAttributeChangeType.Remove,
                StringValue = resolvedRemovalValue
            };
        }

        // Single-valued reference removal: an all-null clearing Update. Staged only because the target still
        // holds the deleted reference (the orchestrator's existence query matched it).
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = flow.TargetAttribute,
            AttributeId = flow.TargetAttribute.Id,
            ChangeType = PendingExportAttributeChangeType.Update
        };
    }

    /// <summary>
    /// Decides how recall changes combine with the Pending Export already attached to the target CSO, per the
    /// #1003 collision policy: an existing Delete wins (deprovisioning supersedes membership updates), an
    /// existing Create is protected (recall never provisions; defensive, unreachable after the
    /// pending-provisioning filter), and an existing Update merges into <paramref name="recallChangesByMergeKey"/>
    /// with recall winning a merge-key collision. Surviving existing changes are cloned with fresh ids
    /// (the orchestrator's delete-then-create persistence removes the old rows), and changes whose unresolved
    /// reference is a deleted object are purged: they can never resolve. Pure in-memory mutation of the
    /// dictionary; nothing is persisted.
    /// </summary>
    /// <param name="recallChangesByMergeKey">The recall changes staged for the CSO, keyed by merge key;
    /// mutated in place when an existing Update export's changes merge in.</param>
    /// <param name="existingPendingExport">The Pending Export already attached to the CSO, if any, with its
    /// attribute value changes loaded.</param>
    /// <param name="deletedMvoIds">The Metaverse Objects deleted in this operation.</param>
    public RecallPendingExportMergeResult MergeRecallChangesWithExistingPendingExport(
        Dictionary<string, PendingExportAttributeValueChange> recallChangesByMergeKey,
        PendingExport? existingPendingExport,
        HashSet<Guid> deletedMvoIds)
    {
        if (existingPendingExport == null)
            return new RecallPendingExportMergeResult { Outcome = RecallPendingExportMergeOutcome.Proceed };

        if (existingPendingExport.ChangeType == PendingExportChangeType.Delete)
            return new RecallPendingExportMergeResult { Outcome = RecallPendingExportMergeOutcome.SkippedDeleteSupersedes };

        if (existingPendingExport.ChangeType == PendingExportChangeType.Create)
            return new RecallPendingExportMergeResult { Outcome = RecallPendingExportMergeOutcome.SkippedCreateProtected };

        var purgedCount = 0;
        foreach (var existingChange in existingPendingExport.AttributeValueChanges)
        {
            if (existingChange.UnresolvedReferenceValue != null &&
                Guid.TryParse(existingChange.UnresolvedReferenceValue, out var unresolvedMvoId) &&
                deletedMvoIds.Contains(unresolvedMvoId))
            {
                purgedCount++;
                continue;
            }

            var mergeKey = GetAttributeChangeMergeKey(existingChange);
            if (recallChangesByMergeKey.ContainsKey(mergeKey))
                continue;

            recallChangesByMergeKey[mergeKey] = new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = existingChange.AttributeId,
                Attribute = existingChange.Attribute,
                StringValue = existingChange.StringValue,
                DateTimeValue = existingChange.DateTimeValue,
                IntValue = existingChange.IntValue,
                LongValue = existingChange.LongValue,
                DecimalValue = existingChange.DecimalValue,
                ByteValue = existingChange.ByteValue,
                GuidValue = existingChange.GuidValue,
                BoolValue = existingChange.BoolValue,
                UnresolvedReferenceValue = existingChange.UnresolvedReferenceValue,
                ResolvedReferenceCsoId = existingChange.ResolvedReferenceCsoId,
                ChangeType = existingChange.ChangeType
            };
        }

        return new RecallPendingExportMergeResult
        {
            Outcome = RecallPendingExportMergeOutcome.Proceed,
            PurgedChangeCount = purgedCount
        };
    }

    /// <summary>
    /// Removes from a Pending Export every attribute value change whose unresolved reference is one of the
    /// deleted Metaverse Objects: the deleted object had no presence in that target system, so the removal is
    /// a no-op there, and the reference could never resolve at export time. Comparison is case-insensitive
    /// because unresolved reference strings are not guaranteed a casing at every producer. Pure in-memory
    /// mutation; returns how many changes were removed.
    /// </summary>
    /// <param name="pendingExport">The Pending Export to purge, mutated in place.</param>
    /// <param name="deletedMvoIds">The Metaverse Objects deleted in this operation.</param>
    public int PurgeChangesReferencingDeletedObjects(PendingExport pendingExport, HashSet<Guid> deletedMvoIds)
    {
        var unresolvable = pendingExport.AttributeValueChanges
            .Where(avc => avc.UnresolvedReferenceValue != null &&
                          Guid.TryParse(avc.UnresolvedReferenceValue, out var unresolvedMvoId) &&
                          deletedMvoIds.Contains(unresolvedMvoId))
            .ToList();

        foreach (var change in unresolvable)
            pendingExport.AttributeValueChanges.Remove(change);

        return unresolvable.Count;
    }
}
