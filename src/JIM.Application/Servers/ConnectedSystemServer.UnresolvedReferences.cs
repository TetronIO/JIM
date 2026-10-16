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
        var targets = AmbiguityTargetsOf(connectedSystem);
        var objectType = (connectedSystem.ObjectTypes ?? []).FirstOrDefault(type => type.Id == typeId);
        var candidateAttributeIds = AmbiguityCandidateAttributeIdsOf(objectType, targets.Count);

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

        var ambiguous = await FindAmbiguousReferenceValuesAsync(context.ConnectedSystemId, context.AmbiguityTargets, values
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
            var ambiguous = await FindAmbiguousReferenceValuesAsync(context.ConnectedSystemId, context.AmbiguityTargets, candidates.Select(v => v.Value));
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
        DescribeAmbiguityOfTheGroup(summary.Fixes);
        return summary;
    }

    /// <summary>
    /// How many unresolved reference values a Connected System's objects hold, and on how many objects: the cheap read
    /// behind the Connected System page's signpost.
    /// </summary>
    public async Task<ConnectedSystemUnresolvedReferenceTotals> GetUnresolvedReferenceTotalsAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetUnresolvedReferenceTotalsAsync(connectedSystemId);
    }

    /// <summary>
    /// Every unresolved reference value across a Connected System, summarised by likely cause and next step with the
    /// values and objects each addresses ("Where they point"), or null where the Connected System does not exist.
    /// Judged against the Connected System's current Container Scope, exactly as one object's summary is.
    /// </summary>
    public async Task<ConnectedSystemUnresolvedReferenceSummary?> GetConnectedSystemUnresolvedReferenceSummaryAsync(int connectedSystemId)
    {
        var connectedSystem = await GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return null;

        var repository = Application.Repository.ConnectedSystems;
        var totals = await repository.GetUnresolvedReferenceTotalsAsync(connectedSystemId);
        var summary = new ConnectedSystemUnresolvedReferenceSummary
        {
            ConnectedSystemId = connectedSystemId,
            UnresolvedReferenceHandling = connectedSystem.UnresolvedReferenceHandling,
            TotalCount = totals.ReferenceCount,
            ObjectCount = totals.ObjectCount
        };
        if (totals.ReferenceCount == 0)
            return summary;

        var classifier = new UnresolvedReferenceClassifier(connectedSystem, GetConnectorContainment(connectedSystem));
        var entries = classifier.CanPlaceValues
            ? await PlaceConnectedSystemValuesAsync(connectedSystemId, classifier)
            : await SplitConnectedSystemValuesAsync(connectedSystem, classifier, totals);

        var steps = Summarise(totals.ReferenceCount, [.. entries.Select(e => (e.Diagnosis, e.Count))]);
        foreach (var fix in steps.Fixes)
        {
            // A step standing for one group already knows its objects. One standing for several parents asks the
            // database, because an object with values under two of them must be counted once.
            var addressed = entries.Where(e => IsAddressedBy(fix, e.Diagnosis)).ToList();
            fix.ObjectCount = addressed.Count == 1
                ? addressed[0].ObjectCount
                : await repository.GetUnresolvedReferenceObjectCountAsync(connectedSystemId, [.. addressed.Select(e => e.ParentKey!)]);
        }

        DescribeAmbiguityOfTheGroup(steps.Fixes);
        summary.Causes = steps.Causes;
        summary.Fixes = steps.Fixes;
        summary.LargestFix = steps.LargestFix;
        return summary;
    }

    /// <summary>
    /// A Connected System's unresolved reference values, one diagnosis per parent they sit under, for a Connector whose
    /// values name their Containers.
    /// </summary>
    private async Task<List<(ConnectedSystemObjectUnresolvedReference Diagnosis, int Count, int ObjectCount, string? ParentKey)>> PlaceConnectedSystemValuesAsync(
        int connectedSystemId,
        UnresolvedReferenceClassifier classifier)
    {
        var groups = await Application.Repository.ConnectedSystems.GetConnectedSystemUnresolvedReferenceValueGroupsAsync(connectedSystemId);
        return [.. groups.Select(group => (classifier.Diagnose(string.Empty, group.SampleValue), group.Count, group.ObjectCount, (string?)group.ParentKey))];
    }

    /// <summary>
    /// A Connected System's unresolved reference values split between those ambiguous across Object Types and those
    /// simply not found, for a Connector without Containers. Each distinct candidate value is checked once against the
    /// connector space, as one object's summary checks its own.
    /// </summary>
    private async Task<List<(ConnectedSystemObjectUnresolvedReference Diagnosis, int Count, int ObjectCount, string? ParentKey)>> SplitConnectedSystemValuesAsync(
        ConnectedSystem connectedSystem,
        UnresolvedReferenceClassifier classifier,
        ConnectedSystemUnresolvedReferenceTotals totals)
    {
        var repository = Application.Repository.ConnectedSystems;
        var notFound = classifier.Diagnose(string.Empty, string.Empty);
        List<(ConnectedSystemObjectUnresolvedReference Diagnosis, int Count, int ObjectCount, string? ParentKey)> everyValueNotFound =
            [(notFound, totals.ReferenceCount, totals.ObjectCount, null)];

        var targets = AmbiguityTargetsOf(connectedSystem);
        var candidateAttributeIds = (connectedSystem.ObjectTypes ?? [])
            .SelectMany(type => AmbiguityCandidateAttributeIdsOf(type, targets.Count))
            .ToHashSet();
        if (candidateAttributeIds.Count == 0)
            return everyValueNotFound;

        var candidates = await repository.GetDistinctUnresolvedReferenceValuesAsync(connectedSystem.Id, candidateAttributeIds);
        var ambiguous = await FindAmbiguousReferenceValuesAsync(connectedSystem.Id, targets, candidates);
        if (ambiguous.Count == 0)
            return everyValueNotFound;

        // The values exactly as stored, so the database counts precisely the ones found ambiguous.
        var ambiguousValues = candidates.Where(ambiguous.ContainsKey).ToList();
        var split = await repository.GetUnresolvedReferenceValueSplitAsync(connectedSystem.Id, candidateAttributeIds, ambiguousValues);
        var candidateTypeNames = ambiguous.Values.SelectMany(names => names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Every ambiguous value matched at least two types, so their union always names two or more.
        List<(ConnectedSystemObjectUnresolvedReference Diagnosis, int Count, int ObjectCount, string? ParentKey)> entries =
            [(classifier.Diagnose(string.Empty, string.Empty, candidateTypeNames), split.MatchingReferenceCount, split.MatchingObjectCount, null)];
        if (split.OtherReferenceCount > 0)
            entries.Add((notFound, split.OtherReferenceCount, split.OtherObjectCount, null));

        return entries;
    }

    /// <summary>
    /// Whether a diagnosis is one of the values a step addresses: the same key <see cref="Summarise"/> merges by.
    /// </summary>
    private static bool IsAddressedBy(UnresolvedReferenceFix fix, ConnectedSystemObjectUnresolvedReference diagnosis) =>
        fix.Cause == diagnosis.Cause &&
        fix.ContainerExternalId == diagnosis.ContainerExternalId &&
        fix.PartitionExternalId == diagnosis.PartitionExternalId;

    /// <summary>
    /// Values on several attributes can be ambiguous, so the per-value sentence (which names one attribute) does not fit
    /// a step standing for many; say it of them all. LargestFix is one of the Fixes, so this reaches both.
    /// </summary>
    private static void DescribeAmbiguityOfTheGroup(IEnumerable<UnresolvedReferenceFix> fixes)
    {
        foreach (var fix in fixes.Where(f => f.Cause == UnresolvedReferenceCause.AmbiguousObjectType))
        {
            fix.Finding = "These values match objects of more than one Object Type, on attributes that do not declare which one they reference.";
            fix.Remedy = "Declare each attribute's target Object Type on the Schema tab if the Connector supports doing so.";
        }
    }

    /// <summary>
    /// The Object Types a reference could name, by their anchor. A type whose anchor is not exactly one attribute cannot
    /// be the target of a lookup, so it is left out rather than allowed to fail the read.
    /// </summary>
    private static List<(int AnchorAttributeId, string ObjectTypeName)> AmbiguityTargetsOf(ConnectedSystem connectedSystem) =>
        [.. (connectedSystem.ObjectTypes ?? [])
            .Select(type => (Type: type, Anchors: type.Attributes.Where(a => a.IsExternalId).ToList()))
            .Where(pair => pair.Anchors.Count == 1)
            .Select(pair => (pair.Anchors[0].Id, pair.Type.Name))];

    /// <summary>
    /// An Object Type's attributes whose values can be ambiguous (#1285). Ambiguity is only possible where the import
    /// resolves against Object Type anchors without a declared target: an object whose type has a text secondary external
    /// ID resolves its undeclared references by that instead, across every type at once, and a declared target names
    /// exactly one type. With fewer than two anchored types there is nothing to be ambiguous between.
    /// </summary>
    private static IReadOnlySet<int> AmbiguityCandidateAttributeIdsOf(ConnectedSystemObjectType? objectType, int targetCount)
    {
        var resolvesBySecondaryExternalId = objectType?.Attributes.Any(a => a.IsSecondaryExternalId && a.Type == AttributeDataType.Text) ?? false;
        if (objectType == null || resolvesBySecondaryExternalId || targetCount < 2)
            return new HashSet<int>();

        return objectType.Attributes
            .Where(a => a.Type == AttributeDataType.Reference && a.ReferencedObjectTypeId == null)
            .Select(a => a.Id)
            .ToHashSet();
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
                Finding = group.First().Diagnosis.Finding,
                Remedy = group.First().Diagnosis.Remedy
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
        int connectedSystemId,
        IReadOnlyList<(int AnchorAttributeId, string ObjectTypeName)> targets,
        IEnumerable<string> values)
    {
        var distinctValues = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ambiguous = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (distinctValues.Count == 0 || targets.Count < 2)
            return ambiguous;

        var typeNamesByValue = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (anchorAttributeId, objectTypeName) in targets)
        {
            for (var start = 0; start < distinctValues.Count; start += UnresolvedReferenceAmbiguityBatchSize)
            {
                var batch = distinctValues.GetRange(start, Math.Min(UnresolvedReferenceAmbiguityBatchSize, distinctValues.Count - start));
                var matches = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(
                    connectedSystemId, anchorAttributeId, batch);

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
