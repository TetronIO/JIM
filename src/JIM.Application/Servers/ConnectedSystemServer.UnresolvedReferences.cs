// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;

namespace JIM.Application.Servers;

/// <summary>
/// Reading one Connected System Object's unresolved reference values with their likely causes (#959), for the portal,
/// the REST API and PowerShell alike, so all three say the same thing.
/// </summary>
/// <remarks>
/// The causes are <see cref="UnresolvedReferenceClassifier"/>'s; what this adds is everything the classifier cannot
/// know on its own. The Connected System's Container Scope and the Connector's containment rule feed it, and
/// ambiguity across Object Types (#1285) is decided against the connector space exactly as the import decides it, by
/// looking the value up against every candidate Object Type's anchor. Nothing here runs on the import or
/// synchronisation path.
/// </remarks>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// How many values one anchor lookup asks for when checking ambiguity, matching the import's own batching.
    /// </summary>
    private const int UnresolvedReferenceAmbiguityBatchSize = 1000;

    /// <summary>
    /// Prepares what reading a Connected System Object's unresolved references needs, or null where the object does
    /// not exist in the Connected System. Build it once per page or request and pass it to each read.
    /// </summary>
    public async Task<ConnectedSystemObjectUnresolvedReferenceContext?> GetUnresolvedReferenceContextAsync(int connectedSystemId, Guid connectedSystemObjectId)
    {
        var objectTypeId = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectTypeIdAsync(connectedSystemId, connectedSystemObjectId);
        if (objectTypeId is not { } typeId)
            return null;

        var connectedSystem = await GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return null;

        var classifier = new UnresolvedReferenceClassifier(connectedSystem, GetConnectorContainment(connectedSystem));

        // The Object Types a reference could name, by their anchor; a type whose anchor is not exactly one attribute
        // cannot be the target of a lookup, so it is left out rather than allowed to fail the read.
        var objectTypes = connectedSystem.ObjectTypes ?? [];
        var targets = objectTypes
            .Select(type => (Type: type, Anchors: type.Attributes.Where(a => a.IsExternalId).ToList()))
            .Where(pair => pair.Anchors.Count == 1)
            .Select(pair => (AnchorAttributeId: pair.Anchors[0].Id, ObjectTypeName: pair.Type.Name))
            .ToList();

        // Ambiguity is only possible where the import resolves against Object Type anchors without a declared target:
        // an object whose type has a text secondary external ID resolves its undeclared references by that instead,
        // across every type at once, and a declared target names exactly one type.
        var objectType = objectTypes.FirstOrDefault(type => type.Id == typeId);
        var resolvesBySecondaryExternalId = objectType?.Attributes.Any(a => a.IsSecondaryExternalId && a.Type == AttributeDataType.Text) ?? false;
        IReadOnlySet<int> candidateAttributeIds = objectType == null || resolvesBySecondaryExternalId || targets.Count < 2
            ? new HashSet<int>()
            : objectType.Attributes
                .Where(a => a.Type == AttributeDataType.Reference && a.ReferencedObjectTypeId == null)
                .Select(a => a.Id)
                .ToHashSet();

        return new ConnectedSystemObjectUnresolvedReferenceContext(
            connectedSystemId,
            connectedSystem.Name,
            connectedSystem.UnresolvedReferenceHandling,
            connectedSystemObjectId,
            objectType?.Name,
            classifier,
            candidateAttributeIds,
            targets);
    }

    /// <summary>
    /// How many unresolved reference values one Connected System Object holds: the cheap question to ask before deciding
    /// whether building a <see cref="ConnectedSystemObjectUnresolvedReferenceContext"/> is worth it.
    /// </summary>
    public async Task<int> GetConnectedSystemObjectUnresolvedReferenceCountAsync(Guid connectedSystemObjectId)
    {
        return await Application.Repository.ConnectedSystems.GetUnresolvedReferenceValueCountAsync(connectedSystemObjectId);
    }

    /// <summary>
    /// A window of the object's unresolved reference values, each with its likely cause and next step.
    /// </summary>
    public async Task<RangeResultSet<ConnectedSystemObjectUnresolvedReference>> GetUnresolvedReferencesRangeAsync(
        ConnectedSystemObjectUnresolvedReferenceContext context,
        int offset,
        int count,
        string? searchText = null,
        bool includeTotalCount = true)
    {
        ArgumentNullException.ThrowIfNull(context);

        var window = await Application.Repository.ConnectedSystems.GetUnresolvedReferenceValuesRangeAsync(
            context.ConnectedSystemObjectId, offset, count, searchText, includeTotalCount);

        return new RangeResultSet<ConnectedSystemObjectUnresolvedReference>
        {
            Results = await DiagnoseUnresolvedReferencesAsync(context, window.Results),
            TotalResults = window.TotalResults
        };
    }

    /// <summary>
    /// Diagnoses unresolved reference values the caller already holds (the values an attribute table has loaded, say),
    /// exactly as <see cref="GetUnresolvedReferencesRangeAsync"/> would, returning one diagnosis per value in the same
    /// order.
    /// </summary>
    public async Task<List<ConnectedSystemObjectUnresolvedReference>> DiagnoseUnresolvedReferencesAsync(
        ConnectedSystemObjectUnresolvedReferenceContext context,
        IReadOnlyList<ConnectedSystemObjectUnresolvedReferenceValue> values)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(values);

        var ambiguous = await FindAmbiguousReferenceValuesAsync(context, values
            .Where(v => context.AmbiguityCandidateAttributeIds.Contains(v.AttributeId))
            .Select(v => v.Value));

        return [.. values.Select(v => context.Classifier.Diagnose(
            v.AttributeName,
            v.Value,
            context.AmbiguityCandidateAttributeIds.Contains(v.AttributeId) && ambiguous.TryGetValue(v.Value, out var typeNames) ? typeNames : null))];
    }

    /// <summary>
    /// How many unresolved reference values the object holds, how they split by likely cause, every distinct next step
    /// with how many values it addresses, and the one JIM configuration change that would address the most of them.
    /// </summary>
    public async Task<ConnectedSystemObjectUnresolvedReferenceSummary> GetUnresolvedReferenceSummaryAsync(ConnectedSystemObjectUnresolvedReferenceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var repository = Application.Repository.ConnectedSystems;

        if (context.Classifier.CanPlaceValues)
        {
            // Values sharing a parent sit in the same Containers, so one diagnosis per parent stands for the group:
            // a group with hundreds of members out of scope is summarised from a handful of parents.
            var groups = await repository.GetUnresolvedReferenceValueGroupsAsync(context.ConnectedSystemObjectId);
            var placed = groups.Select(group => (Diagnosis: context.Classifier.Diagnose(string.Empty, group.SampleValue), group.Count)).ToList();
            return Summarise(placed.Sum(p => p.Count), placed);
        }

        var total = await repository.GetUnresolvedReferenceValueCountAsync(context.ConnectedSystemObjectId);
        var ambiguousCount = 0;
        var candidateTypeNames = new List<string>();
        if (context.AmbiguityCandidateAttributeIds.Count > 0)
        {
            var candidates = await repository.GetUnresolvedReferenceValuesAsync(context.ConnectedSystemObjectId, [.. context.AmbiguityCandidateAttributeIds]);
            var ambiguous = await FindAmbiguousReferenceValuesAsync(context, candidates.Select(v => v.Value));
            ambiguousCount = candidates.Count(v => ambiguous.ContainsKey(v.Value));
            candidateTypeNames = [.. ambiguous.Values.SelectMany(names => names).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        var entries = new List<(ConnectedSystemObjectUnresolvedReference Diagnosis, int Count)>();
        // Every ambiguous value matched at least two types, so their union always names two or more.
        if (ambiguousCount > 0)
            entries.Add((context.Classifier.Diagnose(string.Empty, string.Empty, candidateTypeNames), ambiguousCount));
        if (total - ambiguousCount > 0)
            entries.Add((context.Classifier.Diagnose(string.Empty, string.Empty), total - ambiguousCount));

        var summary = Summarise(total, entries);

        // Values on several attributes can be ambiguous, so the per-value sentence (which names one attribute) does
        // not fit the whole group; say it of them all. LargestFix is one of the Fixes, so this reaches both.
        foreach (var fix in summary.Fixes.Where(f => f.Cause == UnresolvedReferenceCause.AmbiguousObjectType))
            fix.Suggestion = "These values match objects of more than one Object Type, on attributes that do not declare which one they reference. " +
                             "Declare each attribute's target Object Type on the Schema tab if the Connector supports doing so.";

        return summary;
    }

    private static ConnectedSystemObjectUnresolvedReferenceSummary Summarise(
        int total,
        IReadOnlyCollection<(ConnectedSystemObjectUnresolvedReference Diagnosis, int Count)> entries)
    {
        var causes = entries
            .GroupBy(e => e.Diagnosis.Cause)
            .Select(group => new UnresolvedReferenceCauseCount
            {
                Cause = group.Key,
                FixLocation = UnresolvedReferenceClassifier.FixLocationFor(group.Key),
                Count = group.Sum(e => e.Count)
            })
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Cause)
            .ToList();

        // The same change can address several groups (two parents beneath one unselected Container), so the groups are
        // merged by the step they need.
        var fixes = entries
            .GroupBy(e => (e.Diagnosis.Cause, e.Diagnosis.ContainerExternalId, e.Diagnosis.PartitionExternalId))
            .Select(group => new UnresolvedReferenceFix
            {
                Cause = group.Key.Cause,
                FixLocation = UnresolvedReferenceClassifier.FixLocationFor(group.Key.Cause),
                ContainerExternalId = group.Key.ContainerExternalId,
                PartitionExternalId = group.Key.PartitionExternalId,
                Count = group.Sum(e => e.Count),
                Suggestion = group.First().Diagnosis.Suggestion
            })
            // Most values first; at equal counts, the steps an administrator can take in JIM before those that lie in
            // the Connected System.
            .OrderByDescending(fix => fix.Count)
            .ThenBy(fix => fix.FixLocation)
            .ThenBy(fix => fix.Cause)
            .ThenBy(fix => fix.ContainerExternalId ?? fix.PartitionExternalId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var largestFix = fixes.FirstOrDefault(fix => fix.FixLocation == UnresolvedReferenceFixLocation.Jim);

        return new ConnectedSystemObjectUnresolvedReferenceSummary { TotalCount = total, Causes = causes, Fixes = fixes, LargestFix = largestFix };
    }

    /// <summary>
    /// The values among <paramref name="values"/> that match objects of more than one candidate Object Type, each with
    /// those types' names, decided as the import decides it: by looking each value up against every candidate type's
    /// anchor.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> FindAmbiguousReferenceValuesAsync(
        ConnectedSystemObjectUnresolvedReferenceContext context,
        IEnumerable<string> values)
    {
        var distinctValues = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ambiguous = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (distinctValues.Count == 0 || context.AmbiguityTargets.Count < 2)
            return ambiguous;

        var typeNamesByValue = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (anchorAttributeId, objectTypeName) in context.AmbiguityTargets)
        {
            for (var start = 0; start < distinctValues.Count; start += UnresolvedReferenceAmbiguityBatchSize)
            {
                var batch = distinctValues.GetRange(start, Math.Min(UnresolvedReferenceAmbiguityBatchSize, distinctValues.Count - start));
                var matches = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(
                    context.ConnectedSystemId, anchorAttributeId, batch);

                foreach (var matchedValue in matches.Keys)
                {
                    if (!typeNamesByValue.TryGetValue(matchedValue, out var typeNames))
                    {
                        typeNames = [];
                        typeNamesByValue[matchedValue] = typeNames;
                    }

                    typeNames.Add(objectTypeName);
                }
            }
        }

        foreach (var (value, typeNames) in typeNamesByValue.Where(pair => pair.Value.Count > 1))
            ambiguous[value] = typeNames;

        return ambiguous;
    }
}
