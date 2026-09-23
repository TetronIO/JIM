// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.CompilerServices;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What changing a Connected System's schema selection would do (#1475, #827 gap G6): which Object Types JIM
/// manages, which of their attributes it imports, and whether obsoleting an object withdraws the Metaverse values
/// it contributed.
///
/// None of these settings changes an answer the synchronisation engine computes about an object. They change what
/// JIM READS, and two of the three have no visible effect at all: everything downstream goes on behaving exactly as
/// it did, over data that has stopped moving, which is the hardest kind of change to picture. The engine is consulted
/// for one question only, whether a Metaverse Object left without connectors becomes eligible for deletion, and it is
/// asked through the evaluator every disconnecting preview shares.
///
/// The three levers, and what each actually does:
///
/// - <b>Deselecting an Object Type</b> takes it out of management (#1474), exactly as deselecting a Partition does.
///   The Connector stops returning its objects, so the next Full Import finds every one of them missing and obsoletes
///   it, and the following synchronisation disconnects the joined ones from their Metaverse Objects. It is refused
///   while an enabled Synchronisation Rule is bound to the type, so the preview reports that as Blocking.
/// - <b>Deselecting an attribute</b> is a freeze. The import reconciles only the attributes it was sent, so values
///   already held for a deselected attribute are left exactly as they are, and any Attribute Flow mapping reading it
///   goes on flowing them.
/// - <b>Remove Contributed Attributes On Obsoletion</b> changes what happens to contributed Metaverse values when
///   an object is obsoleted. Its immediately affected population is the objects already obsolete and still joined,
///   waiting for the synchronisation that will disconnect them.
/// </summary>
public class ConnectedSystemSchemaPreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;
    private readonly ISyncEngine _syncEngine;

    /// <summary>
    /// How a transition is written into a delta row's value columns, so a drill-down reads as the state the object
    /// moves between rather than as an internal transition name.
    /// </summary>
    private const string ImportedValue = "Imported";
    private const string NotImportedValue = "Not imported, values frozen";
    private const string ObsoletedValue = "Obsoleted by the next Full Import";
    private const string WithdrawnValue = "Withdrawn on obsoletion";
    private const string RetainedValue = "Left on the Metaverse Object";

    /// <summary>
    /// How many objects are fetched per call when a delta needs the object's own display material. Batched behind
    /// the population read because Npgsql allows one command per connection, so a stream cannot be held open while
    /// querying inside the loop.
    /// </summary>
    private const int FetchBatchSize = 200;

    public ConnectedSystemSchemaPreviewAdapter(JimApplication application, ISyncEngine syncEngine)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.ConnectedSystemSchema;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(ConnectedSystemSchemaProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<ConnectedSystemSchemaProposal>();
        var stored = await StoredSchemaAsync(context);
        var findings = new List<PreviewValidationFinding>();

        if (stored.Schema.DescribesSameSchemaAs(proposal))
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "The proposed schema selection matches the one this Connected System already has, so nothing " +
                "would change and no impact is counted below.",
                nameof(ConnectedSystemObjectType.Selected)));
            return findings;
        }

        var syncRules = await _application.ConnectedSystems.GetSyncRulesAsync(ConnectedSystemId(context), true) ?? [];

        // Judged over every Object Type the proposal would leave deselected, not only the ones it changes, because
        // that is how the save judges it: a type deselected before the refusal existed, with a rule still enabled
        // against it, is refused on the next save whatever else that save changes.
        findings.AddRange(ValidateNoDeselectedObjectTypeStillManaged(stored, proposal, syncRules));

        foreach (var change in Changes(stored, proposal))
        {
            findings.AddRange(ValidateObjectTypeSelection(change));
            findings.AddRange(ValidateAttributeSelection(change, stored, syncRules));
            findings.AddRange(ValidateObsoletionToggle(change));
        }

        return findings;
    }

    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<ConnectedSystemSchemaProposal>();
        var stored = await StoredSchemaAsync(context);

        if (stored.Schema.DescribesSameSchemaAs(proposal))
            return new PreviewCostEstimate(0);

        // Set-based, and deliberately generous: the cost of a schema change is bounded by the objects of the types
        // it touches, whichever of the three levers moved on each. Every yielded change has moved at least one of
        // them by construction, because that is what the walk's comparison is over, so there is nothing to filter.
        var affected = 0;
        foreach (var change in Changes(stored, proposal))
        {
            affected += await _application.ConnectedSystems
                .GetConnectedSystemObjectCountOfTypeAsync(ConnectedSystemId(context), change.ObjectTypeId);
        }

        return new PreviewCostEstimate(affected);
    }

    public async Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connectedSystemId = ConnectedSystemId(context);
        var counts = new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>();
        var disconnectingIds = new List<Guid>();

        // Counting by streaming the transitions costs one population read per lever that moved rather than a
        // per-object preview. The one exception is the Metaverse consequence of a deselected Object Type, which needs
        // to know what each disconnecting object is joined to, so only that population is fetched.
        await foreach (var transition in TransitionsAsync(context, CancellationToken.None))
        {
            counts[transition.TransitionType] = counts.GetValueOrDefault(transition.TransitionType) + transition.Count;
            if (transition.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject)
                disconnectingIds.AddRange(transition.ConnectedSystemObjectIds);
        }

        var disconnectionsByMetaverseObject = await DisconnectionsByMetaverseObjectAsync(connectedSystemId, disconnectingIds);
        await foreach (var delta in PreviewDeletionEligibilityEvaluator.EvaluateAsync(
                           _application, _syncEngine, connectedSystemId, disconnectionsByMetaverseObject, CancellationToken.None))
            counts[delta.TransitionType] = counts.GetValueOrDefault(delta.TransitionType) + 1;

        return
        [
            .. counts
                .OrderByDescending(count => count.Value)
                .ThenBy(count => count.Key)
                .Select(count => new PreviewImpactCount(count.Key, count.Value, ConnectedSystemId: connectedSystemId))
        ];
    }

    public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connectedSystemId = ConnectedSystemId(context);
        var disconnectionsByMetaverseObject = new Dictionary<Guid, int>();

        await foreach (var transition in TransitionsAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The objects are fetched in batches behind the population read so each delta can carry the name an
            // administrator recognises rather than an identifier.
            foreach (var batch in transition.ConnectedSystemObjectIds.Chunk(FetchBatchSize))
            {
                var objects = await _application.ConnectedSystems
                    .GetConnectedSystemObjectsByIdsNoTrackingAsync(connectedSystemId, batch);

                foreach (var cso in objects)
                {
                    if (transition.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject &&
                        cso.MetaverseObjectId is { } metaverseObjectId)
                    {
                        disconnectionsByMetaverseObject[metaverseObjectId] =
                            disconnectionsByMetaverseObject.GetValueOrDefault(metaverseObjectId) + 1;
                    }

                    yield return new PreviewDelta(
                        transition.TransitionType,
                        ObjectDisplayName: cso.NameOrId,
                        ObjectTypeName: transition.ObjectTypeName,
                        MetaverseObjectId: cso.MetaverseObjectId,
                        ConnectedSystemObjectId: cso.Id,
                        ConnectedSystemId: connectedSystemId,
                        AttributeName: transition.AttributeName,
                        OldValue: transition.OldValue,
                        NewValue: transition.NewValue);
                }
            }
        }

        // The Metaverse consequence, evaluated only once every disconnection is known, because whether an object
        // becomes eligible for deletion depends on how many of its connectors survive.
        await foreach (var delta in PreviewDeletionEligibilityEvaluator.EvaluateAsync(
                           _application, _syncEngine, connectedSystemId, disconnectionsByMetaverseObject, cancellationToken))
            yield return delta;
    }

    /// <summary>
    /// How many of each Metaverse Object's connectors in this Connected System the disconnecting objects account for:
    /// the input the shared deletion-eligibility evaluator takes. Fetched in batches, for the joined objects of a
    /// deselected Object Type only.
    /// </summary>
    private async Task<Dictionary<Guid, int>> DisconnectionsByMetaverseObjectAsync(int connectedSystemId,
        IReadOnlyCollection<Guid> disconnectingIds)
    {
        var disconnectionsByMetaverseObject = new Dictionary<Guid, int>();

        foreach (var batch in disconnectingIds.Chunk(FetchBatchSize))
        {
            var objects = await _application.ConnectedSystems
                .GetConnectedSystemObjectsByIdsNoTrackingAsync(connectedSystemId, batch);

            foreach (var metaverseObjectId in objects
                         .Where(cso => cso.MetaverseObjectId.HasValue)
                         .Select(cso => cso.MetaverseObjectId!.Value))
            {
                disconnectionsByMetaverseObject[metaverseObjectId] =
                    disconnectionsByMetaverseObject.GetValueOrDefault(metaverseObjectId) + 1;
            }
        }

        return disconnectionsByMetaverseObject;
    }

    #region transitions

    /// <summary>
    /// One population and the transition it moves through. Deltas and counts are both derived from this, so a
    /// count can never disagree with the rows behind it.
    /// </summary>
    private sealed record SchemaTransition(
        ActivityRunProfileExecutionItemSyncOutcomeType TransitionType,
        string ObjectTypeName,
        IReadOnlyList<Guid> ConnectedSystemObjectIds,
        string? AttributeName,
        string OldValue,
        string NewValue)
    {
        public int Count => ConnectedSystemObjectIds.Count;
    }

    private async IAsyncEnumerable<SchemaTransition> TransitionsAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var proposal = context.ProposedAs<ConnectedSystemSchemaProposal>();
        var stored = await StoredSchemaAsync(context);

        if (stored.Schema.DescribesSameSchemaAs(proposal))
            yield break;

        var connectedSystemId = ConnectedSystemId(context);

        foreach (var change in Changes(stored, proposal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.SelectionChanged)
            {
                // The whole type arrives or leaves. Its attribute changes are moot either way: everything about it
                // stops being read, or starts, and per-attribute rows beside that would be noise.
                var ids = await _application.ConnectedSystems
                    .GetLiveConnectedSystemObjectIdsOfTypeAsync(connectedSystemId, change.ObjectTypeId);

                if (ids.Count > 0 && change.ProposedSelected)
                {
                    // Only objects still held can be counted; ones JIM has never imported are found by the next
                    // Full Import, and there is nothing to count until it runs.
                    yield return new SchemaTransition(
                        ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported,
                        change.ObjectTypeName, ids, null, NotImportedValue, ImportedValue);
                }
                else if (ids.Count > 0)
                {
                    // Every live object is obsoleted by the next Full Import (#1474). The joined ones are then
                    // disconnected, which is what costs the Metaverse anything; the rest simply leave. Live is the
                    // same population deletion detection walks: an obsolete object is already on its way out, and
                    // one still pending provisioning is never obsoleted as missing.
                    var unjoined = (await _application.ConnectedSystems
                        .GetUnjoinedConnectedSystemObjectIdsOfTypeAsync(connectedSystemId, change.ObjectTypeId)).ToHashSet();
                    var joined = ids.Where(id => !unjoined.Contains(id)).ToList();
                    var leaving = ids.Where(unjoined.Contains).ToList();

                    if (joined.Count > 0)
                    {
                        yield return new SchemaTransition(
                            ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject,
                            change.ObjectTypeName, joined, null, ImportedValue, ObsoletedValue);
                    }

                    if (leaving.Count > 0)
                    {
                        yield return new SchemaTransition(
                            ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope,
                            change.ObjectTypeName, leaving, null, ImportedValue, ObsoletedValue);
                    }
                }
            }
            else if (change.AttributesChanged)
            {
                foreach (var transition in AttributeTransitions(change, stored, connectedSystemId))
                    yield return await transition;
            }

            if (change.ObsoletionChanged)
            {
                var ids = await _application.ConnectedSystems
                    .GetObsoleteJoinedConnectedSystemObjectIdsOfTypeAsync(connectedSystemId, change.ObjectTypeId);

                if (ids.Count > 0)
                {
                    yield return change.ProposedRemoveContributedAttributesOnObsoletion
                        ? new SchemaTransition(
                            ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues,
                            change.ObjectTypeName, ids, null, RetainedValue, WithdrawnValue)
                        : new SchemaTransition(
                            ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues,
                            change.ObjectTypeName, ids, null, WithdrawnValue, RetainedValue);
                }
            }
        }
    }

    /// <summary>
    /// One transition per attribute joining or leaving the selection, over the objects that actually hold a value
    /// for it. An object holding no value has nothing to freeze, and counting it would inflate the answer with
    /// objects the change does not touch.
    /// </summary>
    private IEnumerable<Task<SchemaTransition>> AttributeTransitions(SchemaChange change, StoredSchema stored,
        int connectedSystemId)
    {
        foreach (var attributeId in change.AttributesDeselected)
        {
            yield return BuildAttributeTransitionAsync(
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported,
                change, stored, connectedSystemId, attributeId, ImportedValue, NotImportedValue);
        }

        foreach (var attributeId in change.AttributesSelected)
        {
            yield return BuildAttributeTransitionAsync(
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported,
                change, stored, connectedSystemId, attributeId, NotImportedValue, ImportedValue);
        }
    }

    private async Task<SchemaTransition> BuildAttributeTransitionAsync(
        ActivityRunProfileExecutionItemSyncOutcomeType transitionType, SchemaChange change, StoredSchema stored,
        int connectedSystemId, int attributeId, string oldValue, string newValue)
    {
        var ids = await _application.ConnectedSystems.GetLiveConnectedSystemObjectIdsHoldingAttributeAsync(
            connectedSystemId, change.ObjectTypeId, attributeId);

        return new SchemaTransition(transitionType, change.ObjectTypeName, ids,
            stored.AttributeName(change.ObjectTypeId, attributeId), oldValue, newValue);
    }

    #endregion

    #region validation

    private static IEnumerable<PreviewValidationFinding> ValidateObjectTypeSelection(SchemaChange change)
    {
        if (!change.SelectionChanged)
            yield break;

        if (change.ProposedSelected)
        {
            yield return new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                $"Selecting {change.ObjectTypeName} brings its objects into scope for import on the next Import " +
                "Run Profile.",
                nameof(ConnectedSystemObjectType.Selected));
            yield break;
        }

        // Warning rather than Blocking: taking a type out of management is a legitimate thing to do once nothing
        // manages it. What the administrator needs is to know it is a cascade, and when it happens.
        yield return new PreviewValidationFinding(
            PreviewValidationSeverity.Warning,
            $"Deselecting {change.ObjectTypeName} takes its objects out of management. The next Full Import no " +
            "longer returns them, so the objects already imported become obsolete, and the following synchronisation " +
            "disconnects them from their Metaverse Objects, which may leave those eligible for deletion. The Run " +
            "Profile's deletion limits apply to them as to any other deleted object.",
            nameof(ConnectedSystemObjectType.Selected));
    }

    /// <summary>
    /// Blocking for every Object Type the proposal would leave deselected while an enabled Synchronisation Rule is
    /// still bound to it (#1474), in the same words the save refuses it with.
    /// </summary>
    private static IEnumerable<PreviewValidationFinding> ValidateNoDeselectedObjectTypeStillManaged(StoredSchema stored,
        ConnectedSystemSchemaProposal proposal, IReadOnlyCollection<SyncRule> syncRules)
    {
        foreach (var storedType in stored.Schema.ObjectTypes
                     .Where(objectType => !(proposal.For(objectType.ObjectTypeId)?.Selected ?? objectType.Selected))
                     .OrderBy(objectType => objectType.ObjectTypeId))
        {
            var boundRules = syncRules
                .Where(rule => rule.Enabled && rule.ConnectedSystemObjectTypeId == storedType.ObjectTypeId)
                .Select(rule => rule.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (boundRules.Count > 0)
            {
                yield return new PreviewValidationFinding(
                    PreviewValidationSeverity.Blocking,
                    ObjectTypeDeselectionMessages.StillManaged(storedType.Name, boundRules),
                    nameof(ConnectedSystemObjectType.Selected));
            }
        }
    }

        private static IEnumerable<PreviewValidationFinding> ValidateAttributeSelection(SchemaChange change,
        StoredSchema stored, IReadOnlyCollection<SyncRule> syncRules)
    {
        // A type that is leaving or arriving takes its attributes with it, so per-attribute findings beside that
        // would describe a detail of a change the administrator has already been told the whole of.
        if (change.SelectionChanged || !change.AttributesChanged)
            yield break;

        foreach (var attributeId in change.AttributesDeselected)
        {
            var attributeName = stored.AttributeName(change.ObjectTypeId, attributeId) ?? "This attribute";

            if (stored.IsAnchor(change.ObjectTypeId, attributeId))
            {
                yield return new PreviewValidationFinding(
                    PreviewValidationSeverity.Blocking,
                    $"{attributeName} is the External ID for {change.ObjectTypeName} and cannot be deselected. It " +
                    "is what every imported object is matched to its Connected System Object by.",
                    nameof(ConnectedSystemObjectTypeAttribute.Selected));
                continue;
            }

            yield return new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                $"Deselecting {attributeName} on {change.ObjectTypeName} stops it being imported. The values " +
                "already held for it stay on the Connected System Objects and go on flowing, without ever being " +
                "refreshed again.",
                nameof(ConnectedSystemObjectTypeAttribute.Selected));

            var readingRules = syncRules
                .Where(rule => ReadsAttribute(rule, attributeId))
                .Select(rule => rule.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (readingRules.Count > 0)
            {
                yield return new PreviewValidationFinding(
                    PreviewValidationSeverity.Warning,
                    $"{Count(readingRules.Count, "Attribute Flow mapping")} read {attributeName} and will go on " +
                    $"flowing its frozen values: {string.Join(", ", readingRules)}.",
                    nameof(ConnectedSystemObjectTypeAttribute.Selected));
            }
        }
    }

    private static IEnumerable<PreviewValidationFinding> ValidateObsoletionToggle(SchemaChange change)
    {
        if (!change.ObsoletionChanged)
            yield break;

        yield return new PreviewValidationFinding(
            PreviewValidationSeverity.Warning,
            change.ProposedRemoveContributedAttributesOnObsoletion
                ? $"Obsoleting a {change.ObjectTypeName} object will now withdraw the Metaverse values it " +
                  "contributed. Where another Connected System still contributes the attribute it is handed over; " +
                  "where none does, it is cleared."
                : $"Obsoleting a {change.ObjectTypeName} object will now leave the Metaverse values it contributed " +
                  "in place. They stop tracking anything from that point, and nothing reports them as stale.",
            nameof(ConnectedSystemObjectType.RemoveContributedAttributesOnObsoletion));
    }

    /// <summary>
    /// Whether a Synchronisation Rule reads this Connected System attribute in any of its Attribute Flow mappings,
    /// including as one source of a chained expression.
    /// </summary>
    private static bool ReadsAttribute(SyncRule rule, int attributeId) =>
        rule.AttributeFlowRules.Any(mapping =>
            mapping.Sources.Any(source => source.ConnectedSystemAttributeId == attributeId));

    private static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    #endregion

    #region schema

    /// <summary>
    /// One Object Type's proposed change, with everything the findings and the transitions need already resolved,
    /// so neither has to re-derive it from two proposals and disagree about what moved.
    /// </summary>
    private sealed record SchemaChange(
        int ObjectTypeId,
        string ObjectTypeName,
        bool SelectionChanged,
        bool ProposedSelected,
        bool ObsoletionChanged,
        bool ProposedRemoveContributedAttributesOnObsoletion,
        IReadOnlyList<int> AttributesSelected,
        IReadOnlyList<int> AttributesDeselected)
    {
        public bool AttributesChanged => AttributesSelected.Count > 0 || AttributesDeselected.Count > 0;
    }

    /// <summary>
    /// The stored schema, kept as both the proposal shape the comparison needs and the entities the findings need
    /// for attribute names and anchor flags.
    /// </summary>
    private sealed record StoredSchema(
        ConnectedSystemSchemaProposal Schema,
        IReadOnlyList<ConnectedSystemObjectType> ObjectTypes)
    {
        public string? AttributeName(int objectTypeId, int attributeId) =>
            Attribute(objectTypeId, attributeId)?.Name;

        public bool IsAnchor(int objectTypeId, int attributeId) =>
            Attribute(objectTypeId, attributeId) is { } attribute &&
            (attribute.IsExternalId || attribute.IsSecondaryExternalId);

        private ConnectedSystemObjectTypeAttribute? Attribute(int objectTypeId, int attributeId) =>
            ObjectTypes.FirstOrDefault(objectType => objectType.Id == objectTypeId)?
                .Attributes.FirstOrDefault(attribute => attribute.Id == attributeId);
    }

    private async Task<StoredSchema> StoredSchemaAsync(PreviewContext context)
    {
        var objectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(ConnectedSystemId(context)) ?? [];
        return new StoredSchema(ConnectedSystemSchemaProposal.FromCurrentConfiguration(objectTypes), objectTypes);
    }

    /// <summary>
    /// Every Object Type the proposal actually changes. A type the proposal does not mention is left alone rather
    /// than read as deselected by omission, so a partial payload cannot take a whole Object Type out of management
    /// by being short.
    /// </summary>
    private static IEnumerable<SchemaChange> Changes(StoredSchema stored, ConnectedSystemSchemaProposal proposal)
    {
        foreach (var storedType in stored.Schema.ObjectTypes.OrderBy(objectType => objectType.ObjectTypeId))
        {
            var proposedType = proposal.For(storedType.ObjectTypeId);
            if (proposedType == null || proposedType.DescribesSameSelectionAs(storedType))
                continue;

            yield return new SchemaChange(
                storedType.ObjectTypeId,
                // The stored name, because a preview describes what would happen to objects that exist, and they
                // are the ones an administrator is looking at under the name they have now.
                storedType.Name,
                SelectionChanged: proposedType.Selected != storedType.Selected,
                ProposedSelected: proposedType.Selected,
                ObsoletionChanged: proposedType.RemoveContributedAttributesOnObsoletion !=
                                   storedType.RemoveContributedAttributesOnObsoletion,
                ProposedRemoveContributedAttributesOnObsoletion: proposedType.RemoveContributedAttributesOnObsoletion,
                AttributesSelected: proposedType.AttributesSelectedBeyond(storedType),
                AttributesDeselected: proposedType.AttributesDeselectedFrom(storedType));
        }
    }

    private static int ConnectedSystemId(PreviewContext context) =>
        context.TargetId ?? throw new InvalidOperationException(
            "A Connected System schema preview must name the Connected System it is for.");

    #endregion
}
