// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers.Preview;
using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using Serilog;
using System.Runtime.CompilerServices;

namespace JIM.Application.Servers;

/// <summary>
/// The read-only twin of <see cref="ExecuteSynchronisedDeprovisioningAsync"/> (#134): what deleting a Connected System
/// through synchronisation would do, reported per object as preview deltas, with nothing changed.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Previews deleting the Connected System with Synchronised Deprovisioning (#134, the Connected System deletion
    /// adapter's evaluation). Walks the same two passes the real run does, through the same obsoletion core
    /// (<see cref="ConnectedSystemObjectObsoletionService"/>) and the same recall re-election, so the answer cannot drift
    /// from what the deletion then does:
    /// <list type="number">
    /// <item><b>Per-object pass</b>: every Connected System Object of the system, page by page, obsoleted on a preview-owned
    /// clone. Recalled values, values taken over by a surviving contributor, Metaverse Object deletion eligibility, values
    /// kept by an Object Type with recall switched off, and the export consequences (corrective updates, and
    /// deprovisioning for export rules the object leaves) are reported.</item>
    /// <item><b>Residue pass</b>: values still carrying the system's provenance on objects the first pass did not reach
    /// (stranded by an earlier Connector Space clear, or kept joined by a RemainJoined rule), recalled by provenance as
    /// the real run's residue pass recalls them.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero side effects is delivered as the Sync Preview engine delivers it (<c>engineering/SYNC_PREVIEW_ZERO_SIDE_EFFECTS.md</c>):
    /// the decisions come from the pure engine; every repository call goes through a <see cref="ReadOnlySyncRepositoryGuard"/>
    /// that throws on any write; each page runs inside a transaction that is always rolled back; and the core mutates only
    /// preview-owned clones of the Connected System Objects and Metaverse Objects it is handed. Deltas are yielded only after
    /// a page's transaction has closed, so a caller persisting them between pages never writes inside it.
    /// </para>
    /// <para>
    /// Deletion eligibility is judged against the state the deletion leaves behind: none of the system's objects remains a
    /// connector. The real run deletes each batch's objects before the next, then marks any Metaverse Object the deletion
    /// orphaned as its final step, so it reaches the same end state; a page-by-page dry run, which deletes nothing, has to
    /// be told it. Values a pending deletion freezes, and values an Object Type with recall switched off keeps, are not
    /// reported as cleared, because the real run does not clear them.
    /// </para>
    /// <para>
    /// Exports targeting the system being deleted are not reported: the real run would stage them, and they would go with
    /// the system moments later.
    /// </para>
    /// </remarks>
    internal async IAsyncEnumerable<PreviewDelta> PreviewSynchronisedDeprovisioningAsync(
        int connectedSystemId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const int batchSize = 500;

        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId)
            ?? throw new InvalidDataException($"PreviewSynchronisedDeprovisioningAsync: Connected System {connectedSystemId} does not exist.");

        // Every read goes through the guard, including the ones made before a page's rollback scope opens.
        var guard = new ReadOnlySyncRepositoryGuard(Application.SyncRepo);

        // The run's support set, exactly as ExecuteSynchronisedDeprovisioningAsync builds it.
        var allSyncRules = await guard.GetAllSyncRulesAsync();
        var systemSyncRules = allSyncRules.Where(rule => rule.ConnectedSystemId == connectedSystemId).ToList();
        var priorityContext = await BuildRecallPriorityContextAsync(allSyncRules);
        var syncEngine = new SyncEngine();
        var expressionEvaluator = new DynamicExpressoEvaluator();
        var recallScope = ContributorRecallScope.ForDeletedConnectedSystem(connectedSystemId);
        var remainingImportSourceEvaluator = new RemainingImportSourceEvaluator(guard);
        var survivorObjectTypes = new List<ConnectedSystemObjectType>();
        var exportEvaluationCache = await new ExportEvaluationServer(Application, guard).BuildExportEvaluationCacheAsync(allSyncRules);

        // What the per-object pass did to each Metaverse Object, so the residue pass leaves alone what the real run's
        // residue pass would find already gone (recalled), frozen (pending deletion) or deleted.
        var outcome = new DeprovisioningPreviewProgress();

        var pass = new DeprovisioningPreviewPass(connectedSystemId, connectedSystem.Name, guard, systemSyncRules, priorityContext,
            syncEngine, expressionEvaluator, recallScope, remainingImportSourceEvaluator, survivorObjectTypes, exportEvaluationCache);

        // Pass A: per-object obsoletion, keyset-paged in ascending id order as the real run pages.
        var knownTotal = await guard.GetConnectedSystemObjectCountAsync(connectedSystemId);
        var cursor = Guid.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await guard.GetConnectedSystemObjectsAsync(
                connectedSystemId, page: 1, pageSize: batchSize, knownTotalCount: knownTotal, lastSyncTimestamp: null, afterId: cursor);
            if (page.Results.Count == 0)
                break;
            cursor = page.Results[^1].Id;

            foreach (var delta in await PreviewDeprovisioningPageAsync(pass, page.Results, outcome))
                yield return delta;
        }

        // Pass B: the residue pass, per import rule in ascending id order, skipping a rule whose Object Type keeps its
        // values by policy exactly as the real residue pass does.
        foreach (var importRule in systemSyncRules.Where(rule => rule.Direction == SyncRuleDirection.Import).OrderBy(rule => rule.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var objectType = await ResolveImportRuleObjectTypeAsync(importRule, connectedSystemId);
            if (!objectType.RemoveContributedAttributesOnObsoletion)
                continue;

            var residueMvoIds = (await guard.GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync(importRule.Id))
                .Where(id => !outcome.Recalled.Contains(id) && !outcome.PendingDeletion.Contains(id) && !outcome.Deleted.Contains(id))
                .ToList();

            foreach (var batch in residueMvoIds.Chunk(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var delta in await PreviewResidueBatchAsync(pass, importRule.Id, batch, outcome))
                    yield return delta;
            }
        }

        Log.Information(
            "PreviewSynchronisedDeprovisioningAsync: Connected System {ConnectedSystemId}: {RecalledCount} Metaverse Object(s) would have values recalled, " +
            "{PendingCount} would become eligible for deletion after a grace period, {DeletedCount} immediately.",
            connectedSystemId, outcome.Recalled.Count, outcome.PendingDeletion.Count, outcome.Deleted.Count);
    }

    /// <summary>
    /// One page of the per-object pass, inside its own rolled-back scope. Returns the page's deltas once the scope has
    /// closed.
    /// </summary>
    private async Task<List<PreviewDelta>> PreviewDeprovisioningPageAsync(
        DeprovisioningPreviewPass pass,
        List<ConnectedSystemObject> page,
        DeprovisioningPreviewProgress progress)
    {
        var deltas = new List<PreviewDelta>();
        await using var rollbackScope = await pass.Guard.BeginRollbackOnlyTransactionAsync();
        var previewExportServer = new ExportEvaluationServer(Application, pass.Guard);

        var joinedMvoIds = page.Where(cso => cso.MetaverseObjectId.HasValue).Select(cso => cso.MetaverseObjectId!.Value).Distinct().ToList();
        if (joinedMvoIds.Count > 0)
            await previewExportServer.RefreshExportEvaluationCacheForPageAsync(pass.ExportEvaluationCache, joinedMvoIds);

        // One clone per Metaverse Object for the page, so two of the system's objects joined to the same identity act on
        // the same working copy, as they act on the same instance in the real run.
        var mvoClones = new Dictionary<Guid, MetaverseObject>();

        foreach (var original in page)
        {
            var cso = CloneForDeprovisioningPreview(original, mvoClones);
            cso.Status = ConnectedSystemObjectStatus.Obsolete;
            var mvo = cso.MetaverseObject;

            var result = await ConnectedSystemObjectObsoletionService.ProcessObsoleteConnectedSystemObjectAsync(
                cso,
                pass.SystemSyncRules,
                pass.RecallScope,
                pass.PriorityContext,
                pass.RemainingImportSourceEvaluator,
                pass.SyncEngine,
                pass.Guard,
                (survivor, rule) => Application.ScopingEvaluation.IsCsoInScopeForImportRule(survivor, rule),
                pass.SurvivorObjectTypes,
                pass.ExpressionEvaluator,
                () => new ActivityRunProfileExecutionItem(),
                ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None,
                (target, disconnectingSystemId, remaining) => Task.FromResult(EvaluateDeletionAtEndState(pass, target, disconnectingSystemId, remaining)),
                recordPreRecallAttributeSnapshot: _ => { });

            if (mvo == null)
                continue;

            if (result.MvoDeletionDecision is { Fate: not MvoDeletionFate.NotDeleted } decision && progress.EligibilityReported.Add(mvo.Id))
            {
                deltas.Add(new PreviewDelta(
                    ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
                    ObjectDisplayName: mvo.NameOrId,
                    ObjectTypeName: mvo.Type?.Name,
                    MetaverseObjectTypeId: mvo.Type?.Id,
                    MetaverseObjectId: mvo.Id,
                    ConnectedSystemId: pass.ConnectedSystemId,
                    AttributeName: PreviewDeletionEligibilityEvaluator.DeletionEligibilityAttributeName,
                    OldValue: PreviewDeletionEligibilityEvaluator.NotDeletionEligible,
                    NewValue: PreviewDeletionEligibilityEvaluator.DescribeDeletionOutcome(decision)));

                if (decision.Fate == MvoDeletionFate.DeletedImmediately)
                {
                    progress.Deleted.Add(mvo.Id);
                    deltas.AddRange(await PreviewDeletionCascadeAsync(pass, mvo));
                }
                else
                {
                    progress.PendingDeletion.Add(mvo.Id);
                }
            }

            if (result.MvoAttributeChange is { } change)
            {
                progress.Recalled.Add(mvo.Id);
                deltas.AddRange(ClassifyRecall(mvo, change.Additions, change.Removals));
            }
            else if (original.Type is { RemoveContributedAttributesOnObsoletion: false } && result.MvoDeletionDecision?.Fate != MvoDeletionFate.DeletedImmediately)
            {
                // Kept by the Object Type's policy, not by accident: say so, once per object, so "nothing was cleared"
                // does not read as "nothing was contributed".
                var keptCount = mvo.AttributeValues.Count(av => av.ContributedBySystemId == pass.ConnectedSystemId && !av.NullValue);
                if (keptCount > 0)
                {
                    deltas.Add(new PreviewDelta(
                        ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues,
                        ObjectDisplayName: mvo.NameOrId,
                        ObjectTypeName: mvo.Type?.Name,
                        MetaverseObjectTypeId: mvo.Type?.Id,
                        MetaverseObjectId: mvo.Id,
                        ConnectedSystemId: pass.ConnectedSystemId,
                        NewValue: keptCount == 1 ? "1 value kept" : $"{keptCount} values kept"));
                }
            }

            if (result.ExportEvaluation is { } exportEvaluation)
            {
                var outbound = await previewExportServer.EvaluateOutboundPreviewForMaterialisedMvosAsync(
                    [exportEvaluation.Mvo], pass.ExportEvaluationCache, (exportEvaluation.ChangedAttributes, exportEvaluation.RemovedAttributes));
                var outboundDeltas = ClassifyOutbound(pass, exportEvaluation.Mvo, outbound).ToList();
                deltas.AddRange(outboundDeltas);
                if (await PreviewLastConnectorDisconnectAsync(pass, exportEvaluation.Mvo, outboundDeltas, progress) is { } eligibility)
                    deltas.Add(eligibility);
            }
        }

        return deltas;
    }

    /// <summary>
    /// One batch of the residue pass for one import rule, inside its own rolled-back scope: mirrors
    /// <see cref="RecallSyncRuleContributedValuesAsync"/> for the deleted-system scope (no preservation gate, objects
    /// pending deletion left alone), on clones.
    /// </summary>
    private async Task<List<PreviewDelta>> PreviewResidueBatchAsync(DeprovisioningPreviewPass pass, int syncRuleId, Guid[] batch,
        DeprovisioningPreviewProgress progress)
    {
        var deltas = new List<PreviewDelta>();
        await using var rollbackScope = await pass.Guard.BeginRollbackOnlyTransactionAsync();
        var previewExportServer = new ExportEvaluationServer(Application, pass.Guard);

        var metaverseObjects = await pass.Guard.GetMetaverseObjectsByIdsNoTrackingAsync(batch);
        await previewExportServer.RefreshExportEvaluationCacheForPageAsync(pass.ExportEvaluationCache, batch);

        foreach (var original in metaverseObjects.Where(mvo => mvo.LastConnectorDisconnectedDate == null))
        {
            var mvo = CloneForDeprovisioningPreview(original);
            var recalledValues = mvo.AttributeValues.Where(av => av.ContributedBySyncRuleId == syncRuleId).ToList();
            if (recalledValues.Count == 0)
                continue;

            mvo.PendingAttributeValueRemovals.AddRange(recalledValues);
            await ContributorReElectionService.ReElectSurvivingContributorsAsync(
                mvo, recalledValues, pass.RecallScope, pass.PriorityContext, pass.SyncEngine, pass.Guard,
                (survivor, rule) => Application.ScopingEvaluation.IsCsoInScopeForImportRule(survivor, rule),
                pass.SurvivorObjectTypes, pass.ExpressionEvaluator);

            var additions = mvo.PendingAttributeValueAdditions.ToList();
            var removals = mvo.PendingAttributeValueRemovals.ToList();
            var changedAttributes = additions.Concat(removals).ToList();
            pass.SyncEngine.ApplyPendingAttributeChanges(mvo);

            deltas.AddRange(ClassifyRecall(mvo, additions, removals));

            var outbound = await previewExportServer.EvaluateOutboundPreviewForMaterialisedMvosAsync(
                [mvo], pass.ExportEvaluationCache, (changedAttributes, removals.ToHashSet()));
            var outboundDeltas = ClassifyOutbound(pass, mvo, outbound).ToList();
            deltas.AddRange(outboundDeltas);
            if (await PreviewLastConnectorDisconnectAsync(pass, mvo, outboundDeltas, progress) is { } eligibility)
                deltas.Add(eligibility);
        }

        return deltas;
    }

    /// <summary>
    /// A scope exit whose Deprovisioning Action is Disconnect breaks the target object's join, and where that leaves the
    /// Metaverse Object with no connector at all, synchronisation marks it as having lost its last one
    /// (<c>ExportEvaluationServer.HandleOutboundDeprovisioningAsync</c>), which housekeeping then deletes under the
    /// type's grace period. Asked of the engine's own rule (<see cref="ISyncEngine.ShouldMarkLastConnectorDisconnected"/>)
    /// against the end state: none of the deleted system's objects, and none of the disconnected targets.
    /// </summary>
    private async Task<PreviewDelta?> PreviewLastConnectorDisconnectAsync(
        DeprovisioningPreviewPass pass, MetaverseObject mvo, IReadOnlyCollection<PreviewDelta> outboundDeltas, DeprovisioningPreviewProgress progress)
    {
        var disconnectedSystemIds = outboundDeltas
            .Where(delta => delta.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject && delta.ConnectedSystemId.HasValue)
            .Select(delta => delta.ConnectedSystemId!.Value)
            .ToList();
        if (disconnectedSystemIds.Count == 0 || progress.EligibilityReported.Contains(mvo.Id))
            return null;

        var remaining = (await pass.Guard.GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(mvo.Id))
            .Where(id => id != pass.ConnectedSystemId)
            .ToList();
        foreach (var disconnectedSystemId in disconnectedSystemIds)
            remaining.Remove(disconnectedSystemId);
        if (remaining.Count > 0)
            return null;

        // A probe carrying no connectors, so the engine is asked the question it asks itself after the removal.
        if (!pass.SyncEngine.ShouldMarkLastConnectorDisconnected(new MetaverseObject { Origin = mvo.Origin, Type = mvo.Type }))
            return null;

        progress.EligibilityReported.Add(mvo.Id);
        progress.PendingDeletion.Add(mvo.Id);
        var gracePeriod = mvo.Type?.DeletionGracePeriod;
        var decision = gracePeriod is { } grace && grace > TimeSpan.Zero
            ? new MvoDeletionDecision { Fate = MvoDeletionFate.DeletionScheduled, GracePeriod = grace }
            : new MvoDeletionDecision { Fate = MvoDeletionFate.DeletedImmediately };

        return new PreviewDelta(
            ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
            ObjectDisplayName: mvo.NameOrId,
            ObjectTypeName: mvo.Type?.Name,
            MetaverseObjectTypeId: mvo.Type?.Id,
            MetaverseObjectId: mvo.Id,
            ConnectedSystemId: disconnectedSystemIds[0],
            AttributeName: PreviewDeletionEligibilityEvaluator.DeletionEligibilityAttributeName,
            OldValue: PreviewDeletionEligibilityEvaluator.NotDeletionEligible,
            NewValue: PreviewDeletionEligibilityEvaluator.DescribeDeletionOutcome(decision));
    }

    /// <summary>
    /// The evaluate-only deletion-rule delegate: the engine's verdict on the state the deletion leaves behind, with none
    /// of the system's objects remaining a connector, and nothing marked.
    /// </summary>
    private static (MvoDeletionDecision Decision, string? PolicySnapshotJson) EvaluateDeletionAtEndState(
        DeprovisioningPreviewPass pass, MetaverseObject mvo, int disconnectingSystemId, IReadOnlyCollection<int> remainingConnectedSystemIds)
    {
        var remainingAtEndState = remainingConnectedSystemIds.Where(id => id != pass.ConnectedSystemId).ToList();
        return (pass.SyncEngine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId, remainingAtEndState, pass.ConnectedSystemName), null);
    }

    /// <summary>
    /// What an immediate deletion does downstream: each of the object's remaining Connected System Objects in other systems
    /// is deprovisioned, disconnected, or has its never-exported provisioning cancelled, by the engine's own decision
    /// (<c>ExportEvaluationServer.EvaluateMvoDeletionsAsync</c> acts on the same verdicts).
    /// </summary>
    private async Task<List<PreviewDelta>> PreviewDeletionCascadeAsync(DeprovisioningPreviewPass pass, MetaverseObject mvo)
    {
        var deltas = new List<PreviewDelta>();
        var joined = (await pass.Guard.GetConnectedSystemObjectsForMvoDeletionAsync([mvo.Id])).GetValueOrDefault(mvo.Id) ?? [];
        var downstream = joined.Where(cso => cso.ConnectedSystemId != pass.ConnectedSystemId).ToList();
        if (downstream.Count == 0)
            return deltas;

        var pendingProvisioningIds = downstream.Where(cso => cso.Status == ConnectedSystemObjectStatus.PendingProvisioning).Select(cso => cso.Id).ToList();
        var existingPendingExports = pendingProvisioningIds.Count > 0
            ? await pass.Guard.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(pendingProvisioningIds)
            : [];

        foreach (var cso in downstream)
        {
            ActivityRunProfileExecutionItemSyncOutcomeType transition;
            if (pass.SyncEngine.IsProvisioningNeverExported(cso, existingPendingExports.GetValueOrDefault(cso.Id)))
                transition = ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled;
            else
                transition = pass.SyncEngine.DecideMvoDeletionExport(cso, mvo.Type?.Id, pass.ExportEvaluationCache.ExportRulesByMvoTypeId, existingPendingExport: null).ShouldStageDeleteExport
                    ? ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport
                    : ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject;

            deltas.Add(new PreviewDelta(transition,
                ObjectDisplayName: mvo.NameOrId,
                ObjectTypeName: mvo.Type?.Name,
                MetaverseObjectTypeId: mvo.Type?.Id,
                MetaverseObjectId: mvo.Id,
                ConnectedSystemObjectId: cso.Id,
                ConnectedSystemId: cso.ConnectedSystemId));
        }

        return deltas;
    }

    /// <summary>
    /// Classifies a recall's change to one Metaverse Object, attribute by attribute: cleared when nothing replaces the
    /// withdrawn value; taken over, with or without a change of value, when a surviving contributor does; withdrawn when
    /// other values of a multi-valued attribute remain.
    /// </summary>
    private static IEnumerable<PreviewDelta> ClassifyRecall(
        MetaverseObject mvo, IReadOnlyCollection<MetaverseObjectAttributeValue> additions, IReadOnlyCollection<MetaverseObjectAttributeValue> removals)
    {
        var clearedAttributeIds = ContributorReElectionService.GetClearedAttributeIds(mvo, additions, removals);

        foreach (var attributeId in removals.Select(av => av.AttributeId).Concat(additions.Select(av => av.AttributeId)).Distinct())
        {
            var removed = removals.Where(av => av.AttributeId == attributeId).ToList();
            var added = additions.Where(av => av.AttributeId == attributeId).ToList();
            var attributeName = removed.Concat(added).Select(av => av.Attribute?.Name).FirstOrDefault(name => name != null)
                ?? $"attribute {attributeId}";
            var oldValue = PreviewValueRenderer.Join(removed.Select(PreviewValueRenderer.Render));
            var newValue = PreviewValueRenderer.Join(added.Select(PreviewValueRenderer.Render));

            ActivityRunProfileExecutionItemSyncOutcomeType transition;
            int? connectedSystemId = removed.Select(av => av.ContributedBySystemId).FirstOrDefault(id => id.HasValue);
            if (clearedAttributeIds.Contains(attributeId))
            {
                transition = ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor;
                newValue = null;
            }
            else if (added.Count > 0)
            {
                transition = oldValue == newValue
                    ? ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue
                    : ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue;
                connectedSystemId = added.Select(av => av.ContributedBySystemId).FirstOrDefault(id => id.HasValue);
            }
            else
            {
                transition = ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues;
                newValue = null;
            }

            yield return new PreviewDelta(transition,
                ObjectDisplayName: mvo.NameOrId,
                ObjectTypeName: mvo.Type?.Name,
                MetaverseObjectTypeId: mvo.Type?.Id,
                MetaverseObjectId: mvo.Id,
                ConnectedSystemId: connectedSystemId,
                AttributeName: attributeName,
                OldValue: oldValue,
                NewValue: newValue);
        }
    }

    /// <summary>
    /// Classifies the outbound decisions for one recalled object: a corrective update per exported attribute, and the
    /// deprovisioning for each export rule the object leaves, named per the rule's Deprovisioning Action. Anything
    /// targeting the system being deleted is left out; it goes with the system.
    /// </summary>
    private static IEnumerable<PreviewDelta> ClassifyOutbound(DeprovisioningPreviewPass pass, MetaverseObject mvo, OutboundPreviewResult outbound)
    {
        foreach (var entry in outbound.Entries.Where(entry => entry.ConnectedSystemId != pass.ConnectedSystemId))
        {
            switch (entry.Kind)
            {
                case OutboundPreviewEntryKind.Staging when entry.ExistingTargetCsoId.HasValue && entry.AttributeChanges.Count > 0:
                    foreach (var changesForAttribute in entry.AttributeChanges.GroupBy(change => change.AttributeId))
                    {
                        var targetCsoId = entry.ExistingTargetCsoId.Value;
                        var attributeName = changesForAttribute.Select(change => change.Attribute?.Name).FirstOrDefault(name => name != null)
                            ?? $"attribute {changesForAttribute.Key}";
                        var current = pass.ExportEvaluationCache.CsoAttributeValues[(targetCsoId, changesForAttribute.Key)]
                            .Select(value => value.ToStringNoName());

                        yield return OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport, mvo, entry, targetCsoId,
                            attributeName, PreviewValueRenderer.Join(current), PreviewValueRenderer.Join(changesForAttribute.Select(PreviewValueRenderer.Render)));
                    }
                    break;

                case OutboundPreviewEntryKind.Deprovisioning when entry.DeprovisioningDecision?.Action == OutOfScopeDeprovisioningAction.StageDeleteExport:
                    yield return OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, mvo, entry, entry.ExistingTargetCsoId);
                    break;

                case OutboundPreviewEntryKind.Deprovisioning when entry.DeprovisioningDecision?.Action == OutOfScopeDeprovisioningAction.Disconnect:
                    yield return OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, mvo, entry, entry.ExistingTargetCsoId);
                    break;

                case OutboundPreviewEntryKind.ProvisioningCancelled:
                    yield return OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled, mvo, entry, entry.ExistingTargetCsoId);
                    break;
            }
        }
    }

    private static PreviewDelta OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType transition, MetaverseObject mvo,
        OutboundPreviewEntry entry, Guid? targetCsoId, string? attributeName = null, string? oldValue = null, string? newValue = null) =>
        new(transition,
            ObjectDisplayName: mvo.NameOrId,
            ObjectTypeName: mvo.Type?.Name,
            MetaverseObjectTypeId: mvo.Type?.Id,
            MetaverseObjectId: mvo.Id,
            ConnectedSystemObjectId: targetCsoId,
            ConnectedSystemId: entry.ConnectedSystemId,
            AttributeName: attributeName,
            OldValue: oldValue,
            NewValue: newValue);

    /// <summary>
    /// A preview-owned copy of a Connected System Object, joined to a preview-owned copy of its Metaverse Object, so the
    /// obsoletion core's join breaking and attribute recall act on copies. The Metaverse Object copy is shared across the
    /// page through <paramref name="mvoClones"/>. Value instances are shared, not copied: the core adds and removes list
    /// entries but never mutates a value in place.
    /// </summary>
    private static ConnectedSystemObject CloneForDeprovisioningPreview(ConnectedSystemObject cso, Dictionary<Guid, MetaverseObject> mvoClones)
    {
        MetaverseObject? mvoClone = null;
        if (cso.MetaverseObject != null && !mvoClones.TryGetValue(cso.MetaverseObject.Id, out mvoClone))
        {
            mvoClone = CloneForDeprovisioningPreview(cso.MetaverseObject);
            mvoClones[mvoClone.Id] = mvoClone;
        }

        var clone = new ConnectedSystemObject
        {
            Id = cso.Id,
            Created = cso.Created,
            LastUpdated = cso.LastUpdated,
            Type = cso.Type,
            TypeId = cso.TypeId,
            ConnectedSystem = cso.ConnectedSystem,
            ConnectedSystemId = cso.ConnectedSystemId,
            PartitionId = cso.PartitionId,
            ExternalIdAttributeId = cso.ExternalIdAttributeId,
            SecondaryExternalIdAttributeId = cso.SecondaryExternalIdAttributeId,
            AttributeValues = [.. cso.AttributeValues],
            Status = cso.Status,
            MetaverseObject = mvoClone,
            MetaverseObjectId = cso.MetaverseObjectId,
            JoinType = cso.JoinType,
            DateJoined = cso.DateJoined
        };
        mvoClone?.ConnectedSystemObjects.Add(clone);
        return clone;
    }

    /// <summary>
    /// A preview-owned copy of a Metaverse Object carrying what the obsoletion core and the deletion rule read: identity,
    /// type, origin, values, and any deletion already pending.
    /// </summary>
    private static MetaverseObject CloneForDeprovisioningPreview(MetaverseObject mvo)
    {
        var clone = new MetaverseObject
        {
            Id = mvo.Id,
            Type = mvo.Type,
            Origin = mvo.Origin,
            Created = mvo.Created,
            CachedDisplayName = mvo.CachedDisplayName,
            LastConnectorDisconnectedDate = mvo.LastConnectorDisconnectedDate,
            DeletionTriggeredBySystemId = mvo.DeletionTriggeredBySystemId
        };
        foreach (var attributeValue in mvo.AttributeValues)
            clone.AttributeValues.Add(attributeValue);
        return clone;
    }

    /// <summary>
    /// The shared, read-only inputs every page of one deprovisioning preview evaluates against; the preview's twin of the
    /// support set <see cref="ExecuteSynchronisedDeprovisioningAsync"/> builds. A private record in the
    /// <see cref="SyncPreviewServer"/> context-record style, because it carries Application services.
    /// </summary>
    private sealed record DeprovisioningPreviewPass(
        int ConnectedSystemId,
        string ConnectedSystemName,
        ReadOnlySyncRepositoryGuard Guard,
        List<SyncRule> SystemSyncRules,
        AttributePriorityContext PriorityContext,
        SyncEngine SyncEngine,
        DynamicExpressoEvaluator ExpressionEvaluator,
        ContributorRecallScope RecallScope,
        RemainingImportSourceEvaluator RemainingImportSourceEvaluator,
        List<ConnectedSystemObjectType> SurvivorObjectTypes,
        ExportEvaluationCache ExportEvaluationCache);

    /// <summary>
    /// What the per-object pass decided per Metaverse Object, kept as ids only so a preview of a large system holds no
    /// object graphs between pages.
    /// </summary>
    private sealed class DeprovisioningPreviewProgress
    {
        /// <summary>Objects whose values the per-object pass recalls; the residue pass finds nothing left on them.</summary>
        public HashSet<Guid> Recalled { get; } = [];

        /// <summary>Objects the deletion would mark for deletion after a grace period; their values are frozen.</summary>
        public HashSet<Guid> PendingDeletion { get; } = [];

        /// <summary>Objects the deletion would delete immediately.</summary>
        public HashSet<Guid> Deleted { get; } = [];

        /// <summary>Objects already reported as becoming eligible for deletion, so none is reported twice.</summary>
        public HashSet<Guid> EligibilityReported { get; } = [];
    }
}
