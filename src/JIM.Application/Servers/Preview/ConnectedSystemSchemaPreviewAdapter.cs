// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.CompilerServices;
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
/// The one adapter that runs no evaluation engine, because none of these settings changes an answer the engine
/// computes. They change what JIM READS. Everything downstream goes on behaving exactly as it did, over data that
/// has stopped moving, and that is precisely why the surface needs a preview: a change with no visible effect at
/// all is the hardest kind to picture.
///
/// The three levers, and what each actually does:
///
/// - <b>Deselecting an Object Type</b> stops it being imported and does nothing else. Deletion detection walks the
///   SELECTED types, so its objects are never compared against an import again: they stay joined and keep
///   contributing the values they last imported. Not a cascade, a freeze. See #1474, where whether that is the
///   right behaviour is being decided; this reports the behaviour in force.
/// - <b>Deselecting an attribute</b> is the same freeze one level down. The import reconciles only the attributes
///   it was sent, so values already held for a deselected attribute are left exactly as they are, and any Attribute
///   Flow mapping reading it goes on flowing them.
/// - <b>Remove Contributed Attributes On Obsoletion</b> changes what happens to contributed Metaverse values when
///   an object is obsoleted. Its immediately affected population is the objects already obsolete and still joined,
///   waiting for the synchronisation that will disconnect them.
/// </summary>
public class ConnectedSystemSchemaPreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;

    /// <summary>
    /// How a freeze is written into a delta row's value columns, so a drill-down reads as the state the object
    /// moves between rather than as an internal transition name.
    /// </summary>
    private const string ImportedValue = "Imported";
    private const string NotImportedValue = "Not imported, values frozen";
    private const string WithdrawnValue = "Withdrawn on obsoletion";
    private const string RetainedValue = "Left on the Metaverse Object";

    /// <summary>
    /// How many objects are fetched per call when a delta needs the object's own display material. Batched behind
    /// the population read because Npgsql allows one command per connection, so a stream cannot be held open while
    /// querying inside the loop.
    /// </summary>
    private const int FetchBatchSize = 200;

    public ConnectedSystemSchemaPreviewAdapter(JimApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
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

        foreach (var change in Changes(stored, proposal))
        {
            findings.AddRange(ValidateObjectTypeSelection(change, syncRules));
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

        // No engine evaluation happens here or in the delta walk, so counting by streaming the transitions costs
        // one population read per lever that moved rather than a per-object preview.
        await foreach (var transition in TransitionsAsync(context, CancellationToken.None))
            counts[transition.TransitionType] = counts.GetValueOrDefault(transition.TransitionType) + transition.Count;

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

                if (ids.Count > 0)
                {
                    yield return change.ProposedSelected
                        ? new SchemaTransition(
                            ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported,
                            change.ObjectTypeName, ids, null, NotImportedValue, ImportedValue)
                        : new SchemaTransition(
                            ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported,
                            change.ObjectTypeName, ids, null, ImportedValue, NotImportedValue);
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

    private static IEnumerable<PreviewValidationFinding> ValidateObjectTypeSelection(SchemaChange change,
        IReadOnlyCollection<SyncRule> syncRules)
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

        // The freeze, said in the words #1474 established. Warning rather than Blocking: it is a legitimate thing
        // to do, and what it needs is for the administrator to know it takes nothing out of management.
        yield return new PreviewValidationFinding(
            PreviewValidationSeverity.Warning,
            $"Deselecting {change.ObjectTypeName} stops its objects being imported and does nothing else. The " +
            "objects already imported stay joined to their Metaverse Objects and go on contributing the values " +
            "they last imported, which will not be refreshed again. Nothing is obsoleted and nothing is " +
            "deprovisioned.",
            nameof(ConnectedSystemObjectType.Selected));

        var boundRules = syncRules
            .Where(rule => rule.ConnectedSystemObjectTypeId == change.ObjectTypeId)
            .Select(rule => rule.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (boundRules.Count > 0)
        {
            yield return new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                $"{Count(boundRules.Count, "Synchronisation Rule")} still manage {change.ObjectTypeName} and will " +
                $"go on running against the frozen objects: {string.Join(", ", boundRules)}. Disable them too if " +
                "the type is genuinely leaving management.",
                nameof(ConnectedSystemObjectType.Selected));
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
