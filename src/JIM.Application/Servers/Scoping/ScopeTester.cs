// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// The Scope tester (#2055): whether an object holding sample values is in scope of a Synchronisation Rule, as of a
/// chosen instant, and when that would change as time passes. The portal's Scope tab, the REST API's
/// <c>scoping-criteria/test</c> and <c>Test-JIMScope</c> all call it.
/// </summary>
/// <remarks>
/// Each sample is typed from the schema and held as an attribute value on a transient Connected System Object (an
/// import rule) or Metaverse Object (an export rule), which <see cref="ScopingEvaluationServer"/> then explains with the
/// evaluation synchronisation runs. So a test cannot reach an outcome synchronisation would not.
/// </remarks>
public static class ScopeTester
{
    /// <summary>
    /// The attributes <paramref name="syncRule"/>'s scoping criteria compare, each once, in the order the criteria
    /// reach them: the inputs a test of its scope takes.
    /// </summary>
    public static IReadOnlyList<AttributeSampleDescriptor> DescribeInputs(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        return ComparedAttributes(syncRule)
            .Select(attribute => new AttributeSampleDescriptor(attribute.Name, attribute.Type, attribute.Plurality))
            .ToList();
    }

    /// <summary>
    /// The values <paramref name="connectedSystemObject"/> holds for each attribute an import rule's scoping criteria
    /// compare, every value of a multi-valued attribute included, so a real object can be tested and then edited.
    /// </summary>
    public static IReadOnlyList<AttributeSample> InputsFromConnectedSystemObject(ConnectedSystemObject connectedSystemObject, SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemObject);
        return ComparedAttributes(syncRule)
            .Select(attribute => new AttributeSample(attribute.Name, connectedSystemObject.AttributeValues
                .Where(value => value.AttributeId == attribute.Id)
                .Select(value => TestSampleValues.FromConnectedSystemValue(value, attribute.Type))
                .Where(value => value != null)
                .Select(value => value!)
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// The values <paramref name="metaverseObject"/> holds for each attribute an export rule's scoping criteria
    /// compare. An asserted-null marker carries no value, as scoping reads it.
    /// </summary>
    public static IReadOnlyList<AttributeSample> InputsFromMetaverseObject(MetaverseObject metaverseObject, SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(metaverseObject);
        return ComparedAttributes(syncRule)
            .Select(attribute => new AttributeSample(attribute.Name, metaverseObject.AttributeValues
                .Where(value => !value.NullValue && (value.AttributeId == attribute.Id || value.Attribute?.Id == attribute.Id))
                .Select(value => TestSampleValues.FromMetaverseValue(value, attribute.Type))
                .Where(value => value != null)
                .Select(value => value!)
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// Tests <paramref name="syncRule"/>'s scoping criteria against an object holding the sample
    /// <paramref name="inputs"/>, as of <paramref name="asOfUtc"/>. An attribute not supplied holds no value.
    /// </summary>
    /// <param name="syncRule">The Synchronisation Rule, with its scoping criteria and their attributes. Its criteria
    /// need not be saved, so an edit can be tried before saving it.</param>
    /// <param name="asOfUtc">The instant relative date criteria resolve against.</param>
    public static ScopeTestResult Test(SyncRule syncRule, IReadOnlyList<AttributeSample> inputs, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        ArgumentNullException.ThrowIfNull(inputs);

        var exporting = syncRule.Direction == SyncRuleDirection.Export;
        var attributes = SchemaAttributes(syncRule);
        var errors = new List<string>();
        var typedInputs = new List<(ScopedAttribute Attribute, List<object> Values)>();
        foreach (var input in inputs.Where(input => input.Values.Count > 0))
        {
            var attribute = attributes.FirstOrDefault(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase));
            if (attribute == null)
            {
                var objectTypeName = exporting ? syncRule.MetaverseObjectType?.Name : syncRule.ConnectedSystemObjectType?.Name;
                errors.Add($"'{input.AttributeName}' has a value, but {(objectTypeName == null ? "the object type" : $"'{objectTypeName}'")} " +
                           "has no attribute of that name, so scoping would never see one.");
                continue;
            }

            typedInputs.Add((attribute, TestSampleValues.Typed($"'{attribute.Name}'", input.Values, attribute.Type, attribute.Plurality, errors)));
        }

        if (errors.Count > 0)
            return new ScopeTestResult { InputErrors = errors };

        var scoping = new ScopingEvaluationServer();
        Func<DateTime, ScopingExplanation> explainAt;
        if (exporting)
        {
            var metaverseObject = new MetaverseObject { Type = syncRule.MetaverseObjectType ?? new MetaverseObjectType() };
            metaverseObject.AttributeValues.AddRange(typedInputs.SelectMany(input => input.Values.Select(value =>
            {
                var held = TestSampleValues.ToMetaverseValue((MetaverseAttribute)input.Attribute.Source, value);
                held.MetaverseObject = metaverseObject;
                return held;
            })));
            explainAt = instant => scoping.ExplainMvoForExportRule(metaverseObject, syncRule, instant);
        }
        else
        {
            var connectedSystemObject = new ConnectedSystemObject { TypeId = syncRule.ConnectedSystemObjectType?.Id ?? 0 };
            connectedSystemObject.AttributeValues.AddRange(typedInputs.SelectMany(input => input.Values.Select(value =>
            {
                var held = TestSampleValues.ToConnectedSystemValue((ConnectedSystemObjectTypeAttribute)input.Attribute.Source, value);
                held.ConnectedSystemObject = connectedSystemObject;
                return held;
            })));
            explainAt = instant => scoping.ExplainCsoForImportRule(connectedSystemObject, syncRule, instant);
        }

        var explanation = explainAt(asOfUtc);
        var dates = typedInputs
            .Where(input => input.Attribute.Type == AttributeDataType.DateTime)
            .ToDictionary(input => input.Attribute.Id, input => input.Values.Cast<DateTime>().ToList());

        return new ScopeTestResult
        {
            Explanation = explanation,
            NextChange = NextChange(syncRule, explanation.Outcome, asOfUtc, dates, instant => explainAt(instant).Outcome)
        };
    }

    /// <summary>
    /// The first instant after <paramref name="asOfUtc"/> at which the rule's outcome differs from
    /// <paramref name="current"/>, the values staying as they are; null if there is none.
    /// </summary>
    /// <remarks>
    /// Only a relative date criterion changes with time, and its boundary only crosses one of the object's dates at
    /// the instants <see cref="CrossingCandidates"/> lists, so the outcome is constant between them. Evaluating at each
    /// in turn therefore finds the exact instant, through the same evaluation synchronisation runs, without having to
    /// reason about how the criteria combine.
    /// </remarks>
    private static ScopeChange? NextChange(SyncRule syncRule, ScopingRuleOutcome current, DateTime asOfUtc,
        Dictionary<int, List<DateTime>> dates, Func<DateTime, ScopingRuleOutcome> outcomeAt)
    {
        // An undetermined rule stops at a criterion that cannot be evaluated, and time does not change that.
        if (current == ScopingRuleOutcome.Undetermined)
            return null;

        var exporting = syncRule.Direction == SyncRuleDirection.Export;
        var candidates = new SortedSet<DateTime>(AllCriteria(syncRule.ObjectScopingCriteriaGroups)
            .Where(criterion => criterion is
            {
                ValueMode: DateCriteriaValueMode.Relative, RelativeCount: not null, RelativeUnit: not null, RelativeDirection: not null
            })
            .SelectMany(criterion =>
                dates.TryGetValue((exporting ? criterion.MetaverseAttribute?.Id : criterion.ConnectedSystemAttribute?.Id) ?? 0, out var values)
                    ? values.SelectMany(value => CrossingCandidates(value, criterion.RelativeCount!.Value, criterion.RelativeUnit!.Value,
                        criterion.RelativeDirection!.Value))
                    : [])
            .Where(instant => instant > asOfUtc));

        return candidates
            .Select(instant => new ScopeChange(instant, outcomeAt(instant)))
            .FirstOrDefault(change => change.Outcome != current);
    }

    /// <summary>
    /// The instants at which a relative boundary can cross <paramref name="value"/>. The boundary is "now" moved by the
    /// offset; for any unit but Hours it is then rounded down to midnight, so it only moves at midnight. The crossing
    /// is near the instant the unrounded boundary equals the value, so the midnights around that (wide enough for a
    /// month's clamping to a shorter month) are where it can happen; for Hours it is that instant, or just after it.
    /// </summary>
    private static IEnumerable<DateTime> CrossingCandidates(DateTime value, int count, RelativeDateUnit unit, RelativeDateDirection direction)
    {
        var offset = direction == RelativeDateDirection.Ago ? -count : count;
        var utcValue = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        DateTime crossing;
        try
        {
            crossing = unit switch
            {
                RelativeDateUnit.Hours => utcValue.AddHours(-offset),
                RelativeDateUnit.Days => utcValue.AddDays(-offset),
                RelativeDateUnit.Weeks => utcValue.AddDays(-offset * 7d),
                RelativeDateUnit.Months => utcValue.AddMonths(-offset),
                RelativeDateUnit.Years => utcValue.AddYears(-offset),
                _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unsupported relative date unit.")
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            // The crossing would fall outside the dates a DateTime can hold, so it never happens.
            yield break;
        }

        if (unit == RelativeDateUnit.Hours)
        {
            yield return crossing;
            if (crossing < DateTime.MaxValue)
                yield return crossing.AddTicks(1);
            yield break;
        }

        // Leave room for the days either side; a crossing that close to the ends of the calendar never happens.
        if (crossing.Year is <= 1 or >= 9999)
            yield break;

        for (var day = -1; day <= 3; day++)
            yield return DateTime.SpecifyKind(crossing.Date.AddDays(day), DateTimeKind.Utc);
    }

    /// <summary>An attribute a test can give a value, from either side's schema.</summary>
    private sealed record ScopedAttribute(int Id, string Name, AttributeDataType Type, AttributePlurality Plurality, object Source);

    /// <summary>
    /// The attributes the criteria compare, then the rest of the scoped object type's schema: a sample may be given
    /// for any attribute the object can hold, though only the compared ones change the outcome.
    /// </summary>
    private static List<ScopedAttribute> SchemaAttributes(SyncRule syncRule)
    {
        var attributes = ComparedAttributes(syncRule);
        var schema = syncRule.Direction == SyncRuleDirection.Export
            ? syncRule.MetaverseObjectType?.Attributes.Select(Scoped) ?? []
            : syncRule.ConnectedSystemObjectType?.Attributes.Select(Scoped) ?? [];
        attributes.AddRange(schema.Where(attribute => attributes.All(a => a.Id != attribute.Id)));
        return attributes;
    }

    /// <summary>The attributes the scoping criteria compare on the scoped side, each once, in criteria order.</summary>
    private static List<ScopedAttribute> ComparedAttributes(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        var exporting = syncRule.Direction == SyncRuleDirection.Export;
        return AllCriteria(syncRule.ObjectScopingCriteriaGroups)
            .Select(criterion => exporting
                ? criterion.MetaverseAttribute is { } metaverseAttribute ? Scoped(metaverseAttribute) : null
                : criterion.ConnectedSystemAttribute is { } connectedSystemAttribute ? Scoped(connectedSystemAttribute) : null)
            .Where(attribute => attribute != null)
            .Select(attribute => attribute!)
            .DistinctBy(attribute => attribute.Id)
            .ToList();
    }

    private static ScopedAttribute Scoped(MetaverseAttribute attribute) =>
        new(attribute.Id, attribute.Name, attribute.Type, attribute.AttributePlurality, attribute);

    private static ScopedAttribute Scoped(ConnectedSystemObjectTypeAttribute attribute) =>
        new(attribute.Id, attribute.Name, attribute.Type, attribute.AttributePlurality, attribute);

    /// <summary>Every criterion in the groups, in the order the evaluator reaches them: a group's criteria, then its child groups.</summary>
    private static IEnumerable<SyncRuleScopingCriteria> AllCriteria(IEnumerable<SyncRuleScopingCriteriaGroup> groups) =>
        groups.SelectMany(group => group.Criteria.Concat(AllCriteria(group.ChildGroups)));
}
