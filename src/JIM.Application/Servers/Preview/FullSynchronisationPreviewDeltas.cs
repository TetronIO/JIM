// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Sync;
using JIM.Models.Transactional;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// The rows a Full Synchronisation preview states for one item of the walk (#1530): one per consequence, read off the
/// facts the walk established rather than inferred, so each row is something the run does (the fidelity tests pair
/// them). The vocabulary is the one every Configuration Change Preview speaks, so a deletion or a deprovisioning reads
/// the same here as in the Connected System deletion preview.
/// </summary>
/// <remarks>
/// <para>Where each row comes from:</para>
/// <list type="bullet">
/// <item>Projection and join: the inbound summary's decision.</item>
/// <item>Disconnection, a retained join, a deleted record and a Metaverse Object's deletion: the outcome tree, which is
/// the tree the run records (for an obsolete object, the run's own obsoletion core recorded it).</item>
/// <item>A Metaverse value changed or cleared: the inbound summary's changes, per attribute; not for a projection,
/// whose values are all new and are its projection.</item>
/// <item>Drift corrected: the drift corrections, per attribute.</item>
/// <item>Provisioning, an update to a target, a disconnection or a cancelled provisioning there: the outbound
/// decisions; deprovisioning: the proposed Delete exports, which include an immediate deletion's cascade.</item>
/// </list>
/// <para>
/// Each row's subject (what <see cref="SubjectOf"/> counts) is the object it happens to: the synchronised object, its
/// Metaverse Object, or the object in a target system, told apart by system so one identity's accounts in two systems
/// are two objects. An account that does not exist yet has no id: a provisioning names the Metaverse Object, or, for a
/// projection (whose Metaverse Object does not exist yet either), the object being projected.
/// </para>
/// </remarks>
internal static class FullSynchronisationPreviewDeltas
{
    /// <summary>
    /// The object a row counts against, for "N objects would ..." counts.
    /// </summary>
    internal static (Guid Object, int? System)? SubjectOf(PreviewDelta delta) =>
        (delta.ConnectedSystemObjectId ?? delta.MetaverseObjectId) is { } objectId ? (objectId, delta.ConnectedSystemId) : null;

    /// <summary>
    /// The row for an object the run would skip as unchanged since the last synchronisation.
    /// </summary>
    internal static PreviewDelta Unchanged(FullSyncPreviewItem item, int connectedSystemId) => WouldNotChange(item, connectedSystemId);

    /// <summary>
    /// The rows for an object the run would process or tear down: one per consequence, or a single would-not-change
    /// row where the run would leave it as it is.
    /// </summary>
    /// <param name="item">An evaluated or obsolete item, carrying its preview.</param>
    /// <param name="connectedSystemId">The Connected System being synchronised.</param>
    /// <param name="gracePeriodOf">A Metaverse Object Type's deletion grace period, to say when a scheduled deletion falls.</param>
    internal static List<PreviewDelta> ForObject(FullSyncPreviewItem item, int connectedSystemId, Func<int?, TimeSpan?> gracePeriodOf)
    {
        var preview = item.Preview ?? throw new ArgumentException("An evaluated item carries its preview.", nameof(item));
        var inbound = preview.Inbound;
        var identity = new Identity(
            inbound?.AlreadyJoinedMetaverseObjectId ?? inbound?.WouldJoinMetaverseObjectId,
            inbound?.MetaverseObjectDisplayName ?? item.DisplayName,
            inbound?.MetaverseObjectTypeName ?? item.ObjectTypeName,
            inbound?.MetaverseObjectTypeId);

        var deltas = new List<PreviewDelta>();
        deltas.AddRange(preview.Errors.Select(error => Failure(item, connectedSystemId, error)));

        if (inbound?.WouldProject == true)
            deltas.Add(OwnObject(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, item, identity with { Id = null }, connectedSystemId));
        else if (inbound?.WouldJoinMetaverseObjectId != null)
            deltas.Add(OwnObject(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, item, identity, connectedSystemId));

        deltas.AddRange(FromOutcomeTree(preview.OutcomeTree, item, identity, connectedSystemId, gracePeriodOf));

        if (inbound is { WouldProject: false })
            deltas.AddRange(MetaverseValueChanges(inbound, identity));

        deltas.AddRange(preview.DriftCorrections.Select(correction => new PreviewDelta(
            ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection,
            ObjectDisplayName: identity.Name,
            ObjectTypeName: identity.TypeName,
            MetaverseObjectTypeId: identity.TypeId,
            MetaverseObjectId: identity.Id,
            ConnectedSystemObjectId: item.ConnectedSystemObjectId,
            ConnectedSystemId: connectedSystemId,
            AttributeName: correction.AttributeName,
            OldValue: correction.CurrentValue,
            NewValue: correction.CorrectedValue)));

        // A projection's provisioning names the object being projected, the only thing that exists to name.
        var provisionedFor = inbound?.WouldProject == true ? item.ConnectedSystemObjectId : null;
        deltas.AddRange(Outbound(preview, identity, provisionedFor));

        if (deltas.Count == 0)
            deltas.Add(WouldNotChange(item, connectedSystemId));
        return deltas;
    }

    /// <summary>
    /// The rows for a Metaverse Object the export scope review reaches: what it provisions or deprovisions.
    /// </summary>
    internal static List<PreviewDelta> ForExportScopeReview(FullSyncPreviewItem item)
    {
        var preview = item.Preview ?? throw new ArgumentException("A review item carries its preview.", nameof(item));
        var identity = new Identity(item.MetaverseObjectId, item.DisplayName, item.ObjectTypeName, item.MetaverseObjectTypeId);
        return [.. Outbound(preview, identity, provisionedFor: null)];
    }

    /// <summary>
    /// The structural consequences the run records in its outcome tree: the object leaving its Metaverse Object (out of
    /// scope or obsolete), keeping its join, being deleted unjoined, and the Metaverse Object's deletion with whatever
    /// never-exported provisioning that cancels.
    /// </summary>
    private static IEnumerable<PreviewDelta> FromOutcomeTree(List<SyncOutcomeNode> roots, FullSyncPreviewItem item, Identity identity,
        int connectedSystemId, Func<int?, TimeSpan?> gracePeriodOf)
    {
        foreach (var root in roots)
        {
            switch (root.OutcomeType)
            {
                case ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope:
                case ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected:
                    yield return OwnObject(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, item,
                        identity with { Id = root.TargetEntityId ?? identity.Id }, connectedSystemId);
                    break;

                case ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin:
                    yield return OwnObject(ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin, item,
                        identity with { Id = root.TargetEntityId ?? identity.Id }, connectedSystemId);
                    break;

                // An obsolete object joined to nothing: its record is deleted and nothing else happens.
                case ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted:
                    yield return new PreviewDelta(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted,
                        ObjectDisplayName: item.DisplayName,
                        ObjectTypeName: item.ObjectTypeName,
                        ConnectedSystemObjectId: item.ConnectedSystemObjectId,
                        ConnectedSystemId: connectedSystemId);
                    break;
            }
        }

        foreach (var deletion in Flatten(roots).Where(node => node.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                     or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled))
        {
            var deleted = identity with { Id = deletion.TargetEntityId ?? identity.Id };
            var decision = new MvoDeletionDecision
            {
                Fate = deletion.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                    ? MvoDeletionFate.DeletedImmediately
                    : MvoDeletionFate.DeletionScheduled,
                GracePeriod = gracePeriodOf(deleted.TypeId)
            };
            yield return new PreviewDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
                ObjectDisplayName: deleted.Name,
                ObjectTypeName: deleted.TypeName,
                MetaverseObjectTypeId: deleted.TypeId,
                MetaverseObjectId: deleted.Id,
                AttributeName: PreviewDeletionEligibilityEvaluator.DeletionEligibilityAttributeName,
                OldValue: PreviewDeletionEligibilityEvaluator.NotDeletionEligible,
                NewValue: PreviewDeletionEligibilityEvaluator.DescribeDeletionOutcome(decision));

            // The deletion's cancelled provisionings; its deprovisionings are proposed Delete exports, stated below.
            foreach (var cancelled in deletion.Children.Where(child =>
                         child.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled))
            {
                yield return Target(ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled, deleted,
                    targetObjectId: null, SystemIdOf(cancelled));
            }
        }
    }

    /// <summary>
    /// What happens in target systems: provisioning, updates by attribute, disconnections, cancelled provisioning, and
    /// deprovisioning (including an immediate deletion's cascade, and a cascade's disconnect-only objects).
    /// </summary>
    private static IEnumerable<PreviewDelta> Outbound(SyncPreviewResult preview, Identity identity, Guid? provisionedFor)
    {
        foreach (var entry in preview.OutboundDecisions.Entries)
        {
            switch (entry.Kind)
            {
                case OutboundPreviewEntryKind.Staging when entry.EffectiveChangeType == PendingExportChangeType.Create:
                    yield return Target(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, identity, provisionedFor, entry.ConnectedSystemId);
                    break;

                case OutboundPreviewEntryKind.Staging when entry.EffectiveChangeType.HasValue && entry.AttributeChanges.Count > 0:
                    foreach (var changes in entry.AttributeChanges.GroupBy(change => change.AttributeId))
                    {
                        var attributeName = changes.Select(change => change.Attribute?.Name).FirstOrDefault(name => name != null)
                            ?? $"attribute {changes.Key}";
                        yield return Target(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport, identity,
                            entry.WouldJoinCsoId ?? entry.ExistingTargetCsoId, entry.ConnectedSystemId,
                            attributeName,
                            PreviewValueRenderer.Join(entry.CurrentTargetValues
                                .Where(value => value.AttributeId == changes.Key)
                                .Select(value => value.ToStringNoName())),
                            PreviewValueRenderer.Join(changes.Select(PreviewValueRenderer.Render)));
                    }
                    break;

                case OutboundPreviewEntryKind.Deprovisioning when entry.DeprovisioningDecision?.Action == OutOfScopeDeprovisioningAction.Disconnect:
                    yield return Target(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, identity,
                        entry.ExistingTargetCsoId, entry.ConnectedSystemId);
                    break;

                case OutboundPreviewEntryKind.ProvisioningCancelled:
                    yield return Target(ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled, identity,
                        entry.ExistingTargetCsoId, entry.ConnectedSystemId);
                    break;
            }
        }

        foreach (var deletion in preview.Outbound.ProposedExports.Where(export => export.ChangeType == PendingExportChangeType.Delete))
        {
            yield return Target(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport,
                identity with { Id = deletion.SourceMetaverseObjectId ?? identity.Id }, deletion.ConnectedSystemObjectId, deletion.ConnectedSystemId);
        }

        foreach (var disconnectOnly in preview.Warnings.Where(warning => warning.Code == SyncPreviewMessageCode.DownstreamDisconnectOnly))
        {
            yield return Target(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, identity,
                targetObjectId: null, disconnectOnly.ConnectedSystemId);
        }
    }

    /// <summary>
    /// A Metaverse value changed or cleared, one row per attribute value: what Attribute Flow writes, or a departing
    /// object's values withdrawn (handed to a surviving contributor, or cleared where there is none). Pairs what an
    /// attribute loses with what it gains, which is exact for a single-valued attribute and a reasonable reading for a
    /// multi-valued one; leftovers on either side become one-sided rows.
    /// </summary>
    private static IEnumerable<PreviewDelta> MetaverseValueChanges(SyncPreviewInboundSummary inbound, Identity identity)
    {
        foreach (var changes in inbound.AttributeFlowChanges.GroupBy(change => change.AttributeName, StringComparer.Ordinal))
        {
            var removed = changes.Where(change => !change.IsAddition).Select(change => change.Value).OrderBy(value => value, StringComparer.Ordinal).ToList();
            var added = changes.Where(change => change.IsAddition).Select(change => change.Value).OrderBy(value => value, StringComparer.Ordinal).ToList();
            var unchanged = removed.Intersect(added).ToHashSet();
            removed.RemoveAll(unchanged.Contains);
            added.RemoveAll(unchanged.Contains);

            for (var index = 0; index < Math.Max(removed.Count, added.Count); index++)
            {
                var oldValue = index < removed.Count ? removed[index] : null;
                var newValue = index < added.Count ? added[index] : null;
                yield return new PreviewDelta(
                    newValue == null ? ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor : ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                    ObjectDisplayName: identity.Name,
                    ObjectTypeName: identity.TypeName,
                    MetaverseObjectTypeId: identity.TypeId,
                    MetaverseObjectId: identity.Id,
                    AttributeName: changes.Key,
                    OldValue: oldValue,
                    NewValue: newValue);
            }
        }
    }

    /// <summary>
    /// A blocking error the run would record against the object: an Attribute Flow that would not evaluate, an
    /// ambiguous match, or anything else that fails it. The error's detail is the row's new value, so the drill-down
    /// says why.
    /// </summary>
    private static PreviewDelta Failure(FullSyncPreviewItem item, int connectedSystemId, SyncPreviewMessage error) =>
        new(error.Code switch
            {
                SyncPreviewMessageCode.ExpressionEvaluationError
                    or SyncPreviewMessageCode.MultiValuedToSingleValuedFlow
                    or SyncPreviewMessageCode.GeneratedValueWouldFail => ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow,
                SyncPreviewMessageCode.AmbiguousMatch => ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously,
                _ => ActivityRunProfileExecutionItemSyncOutcomeType.WouldFail
            },
            ObjectDisplayName: item.DisplayName,
            ObjectTypeName: item.ObjectTypeName,
            ConnectedSystemObjectId: item.ConnectedSystemObjectId,
            ConnectedSystemId: connectedSystemId,
            AttributeName: error.AttributeName,
            NewValue: error.Detail);

    private static PreviewDelta WouldNotChange(FullSyncPreviewItem item, int connectedSystemId) =>
        new(ActivityRunProfileExecutionItemSyncOutcomeType.WouldNotChange,
            ObjectDisplayName: item.DisplayName,
            ObjectTypeName: item.ObjectTypeName,
            ConnectedSystemObjectId: item.ConnectedSystemObjectId,
            ConnectedSystemId: connectedSystemId);

    /// <summary>
    /// A consequence for the synchronised object itself, named as the identity it belongs to.
    /// </summary>
    private static PreviewDelta OwnObject(ActivityRunProfileExecutionItemSyncOutcomeType transition, FullSyncPreviewItem item, Identity identity,
        int connectedSystemId) =>
        new(transition,
            ObjectDisplayName: identity.Name,
            ObjectTypeName: identity.TypeName,
            MetaverseObjectTypeId: identity.TypeId,
            MetaverseObjectId: identity.Id,
            ConnectedSystemObjectId: item.ConnectedSystemObjectId,
            ConnectedSystemId: connectedSystemId);

    /// <summary>
    /// A consequence for the identity's object in a target system.
    /// </summary>
    private static PreviewDelta Target(ActivityRunProfileExecutionItemSyncOutcomeType transition, Identity identity, Guid? targetObjectId,
        int? targetSystemId, string? attributeName = null, string? oldValue = null, string? newValue = null) =>
        new(transition,
            ObjectDisplayName: identity.Name,
            ObjectTypeName: identity.TypeName,
            MetaverseObjectTypeId: identity.TypeId,
            MetaverseObjectId: identity.Id,
            ConnectedSystemObjectId: targetObjectId,
            ConnectedSystemId: targetSystemId,
            AttributeName: attributeName,
            OldValue: oldValue,
            NewValue: newValue);

    /// <summary>
    /// The target system an outcome node names: the node carries it as its detail message, as every node the outcome
    /// builders write for a target system does.
    /// </summary>
    private static int? SystemIdOf(SyncOutcomeNode node) => int.TryParse(node.DetailMessage, out var systemId) ? systemId : null;

    private static IEnumerable<SyncOutcomeNode> Flatten(IEnumerable<SyncOutcomeNode> nodes) =>
        nodes.SelectMany(node => Flatten(node.Children).Prepend(node));

    /// <summary>
    /// The Metaverse Object a row is about: its id where it exists, and its name and type as the walk snapshotted them.
    /// </summary>
    private sealed record Identity(Guid? Id, string? Name, string? TypeName, int? TypeId);
}
