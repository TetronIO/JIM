// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// The only implementation of scoping evaluation (#348): synchronisation's in-scope decisions and the explanations
/// shown to administrators both run through <see cref="EvaluateGroup{TSource}"/> and
/// <see cref="EvaluateCriterion{TSource}"/>, so the two cannot disagree.
/// </summary>
/// <remarks>
/// Semantics, which every caller relies on:
/// <list type="bullet">
/// <item><description>Top-level groups are ORed, and synchronisation stops at the first one met.</description></item>
/// <item><description>Within a group every child is evaluated (no short-circuit), then <c>All</c> or <c>Any</c> applies; an empty group is met.</description></item>
/// <item><description>A missing value fails every comparison except Equals against an all-empty absolute criterion.</description></item>
/// <item><description>Only an attribute's first value is compared (#1923).</description></item>
/// <item><description>Relative dates resolve against one instant per evaluation.</description></item>
/// <item><description>An operator invalid for the attribute's type makes synchronisation fail loudly when it is reached.</description></item>
/// </list>
/// The trace is optional and is the only difference between the two modes: when it is null (every synchronisation
/// call) nothing is allocated, invalid criteria throw, and the top-level loop returns at the first met group. When a
/// node is supplied, each node records its outcome, invalid criteria are recorded rather than thrown, and every
/// top-level group is evaluated so the whole tree can be shown; the rule outcome is still the one synchronisation
/// would reach.
/// </remarks>
internal static class ScopingEvaluator
{
    /// <summary>
    /// Whether the object is in scope of a rule with these top-level groups, exactly as synchronisation decides it.
    /// Throws <see cref="InvalidOperationException"/> on reaching an invalid criterion.
    /// </summary>
    internal static bool IsInScope<TSource>(List<SyncRuleScopingCriteriaGroup> topLevelGroups, TSource source, DateTime nowUtc)
        where TSource : struct, IScopingValueSource
    {
        for (var i = 0; i < topLevelGroups.Count; i++)
        {
            if (EvaluateGroup(topLevelGroups[i], source, nowUtc, node: null) == true)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates every top-level group into <paramref name="groupNodes"/> and returns the outcome synchronisation
    /// would reach: in scope at the first met group, undetermined if it would reach an invalid criterion first, out of
    /// scope otherwise.
    /// </summary>
    internal static ScopingRuleOutcome Explain<TSource>(List<SyncRuleScopingCriteriaGroup> topLevelGroups, TSource source, DateTime nowUtc,
        List<ScopingGroupExplanation> groupNodes)
        where TSource : struct, IScopingValueSource
    {
        ScopingRuleOutcome? decided = null;
        for (var i = 0; i < topLevelGroups.Count; i++)
        {
            var node = ScopingTraceBuilder.CreateGroup(topLevelGroups[i], (i + 1).ToString(CultureInfo.InvariantCulture));
            groupNodes.Add(node);

            // Synchronisation stops at the first group that is met, or fails at the first that reaches an invalid
            // criterion; whichever comes first decides. Later groups are still evaluated, for the explanation only.
            var met = EvaluateGroup(topLevelGroups[i], source, nowUtc, node);
            if (decided == null && met != false)
                decided = met == true ? ScopingRuleOutcome.InScope : ScopingRuleOutcome.Undetermined;
        }

        return decided ?? ScopingRuleOutcome.OutOfScope;
    }

    /// <summary>
    /// Evaluates a group and its descendants. Null only when tracing and the group holds an invalid criterion at any
    /// depth (synchronisation would have thrown on reaching it).
    /// </summary>
    private static bool? EvaluateGroup<TSource>(SyncRuleScopingCriteriaGroup group, TSource source, DateTime nowUtc, ScopingGroupExplanation? node)
        where TSource : struct, IScopingValueSource
    {
        var childCount = 0;
        var metCount = 0;
        var undetermined = false;

        var criteria = group.Criteria;
        for (var i = 0; i < criteria.Count; i++)
        {
            childCount++;
            var criterionNode = node == null ? null : ScopingTraceBuilder.AddCriterion(node, criteria[i], childCount);
            var outcome = EvaluateCriterion(criteria[i], source, nowUtc, criterionNode);
            if (criterionNode != null)
                criterionNode.Outcome = outcome;

            if (outcome == ScopingCriterionOutcome.Met)
                metCount++;
            else if (outcome == ScopingCriterionOutcome.Invalid)
                undetermined = true;
        }

        var childGroups = group.ChildGroups;
        for (var i = 0; i < childGroups.Count; i++)
        {
            childCount++;
            var childNode = node == null ? null : ScopingTraceBuilder.AddChildGroup(node, childGroups[i], childCount);
            var met = EvaluateGroup(childGroups[i], source, nowUtc, childNode);
            if (met == true)
                metCount++;
            else if (met == null)
                undetermined = true;
        }

        bool? result = undetermined
            ? null
            : childCount == 0 || group.Type switch
            {
                SearchGroupType.All => metCount == childCount,
                SearchGroupType.Any => metCount > 0,
                _ => false
            };

        if (node != null)
            ScopingTraceBuilder.RecordGroup(node, result, metCount, childCount);

        return result;
    }

    private static ScopingCriterionOutcome EvaluateCriterion<TSource>(SyncRuleScopingCriteria criterion, TSource source, DateTime nowUtc,
        ScopingCriterionExplanation? node)
        where TSource : struct, IScopingValueSource
    {
        if (!source.TryGetAttribute(criterion, out var attributeId, out var type, out var attributeName))
            return ScopingCriterionOutcome.AttributeMissing;

        if (!SearchComparisonOperators.IsValid(criterion.ComparisonType, type))
        {
            if (node == null)
                throw InvalidOperator(criterion.ComparisonType, type, attributeName);

            ScopingTraceBuilder.RecordAttribute(node, criterion, attributeId, type, attributeName, boundary: null, value: null, valueCount: 0);
            return ScopingCriterionOutcome.Invalid;
        }

        var boundary = type == AttributeDataType.DateTime ? ResolveCriterionDate(criterion, nowUtc) : null;
        var hasValue = source.TryGetFirstValue(attributeId, out var value);
        var met = hasValue ? Compare(type, value, criterion, boundary) : MatchesMissingValue(criterion);
        var outcome = met
            ? ScopingCriterionOutcome.Met
            : hasValue ? ScopingCriterionOutcome.NotMet : ScopingCriterionOutcome.NoValue;

        if (node != null)
            ScopingTraceBuilder.RecordAttribute(node, criterion, attributeId, type, attributeName, boundary,
                hasValue ? value : null, source.CountValues(attributeId));

        return outcome;
    }

    /// <summary>
    /// The failure for a criterion whose operator cannot apply to its attribute's type, logged as it is created.
    /// Defence in depth: the write path rejects such criteria, so reaching here means a rule was persisted before that
    /// guard existed or was changed outside it, and silently mis-scoping objects would be worse.
    /// </summary>
    private static InvalidOperationException InvalidOperator(SearchComparisonType comparisonType, AttributeDataType type, string attributeName)
    {
        Log.Error("Scoping evaluation: comparison operator {Operator} is not valid for the {Type} attribute {Attribute}; " +
                  "the Synchronisation Rule is misconfigured", comparisonType, type, attributeName);
        return new InvalidOperationException(
            $"Comparison operator '{comparisonType}' is not valid for the {type} attribute '{attributeName}' on scoping criteria.");
    }

    /// <summary>
    /// Only Equals against an all-empty absolute criterion matches a missing value. A relative date criterion always
    /// resolves to a real boundary, so it never does.
    /// </summary>
    private static bool MatchesMissingValue(SyncRuleScopingCriteria criterion) =>
        criterion.ComparisonType == SearchComparisonType.Equals &&
        criterion.ValueMode == DateCriteriaValueMode.Absolute &&
        criterion.StringValue == null &&
        criterion.IntValue == null &&
        criterion.LongValue == null &&
        criterion.DecimalValue == null &&
        criterion.DateTimeValue == null &&
        criterion.BoolValue == null &&
        criterion.GuidValue == null;

    /// <summary>
    /// The DateTime a date criterion compares against: its relative boundary resolved against
    /// <paramref name="nowUtc"/> when Relative, otherwise its fixed value.
    /// </summary>
    internal static DateTime? ResolveCriterionDate(SyncRuleScopingCriteria criterion, DateTime nowUtc)
    {
        if (criterion.ValueMode == DateCriteriaValueMode.Relative &&
            criterion.RelativeCount.HasValue && criterion.RelativeUnit.HasValue && criterion.RelativeDirection.HasValue)
        {
            return RelativeDateResolver.Resolve(criterion.RelativeCount.Value, criterion.RelativeUnit.Value, criterion.RelativeDirection.Value, nowUtc);
        }

        return criterion.DateTimeValue;
    }

    private static bool Compare(AttributeDataType type, in ScopingValue value, SyncRuleScopingCriteria criterion, DateTime? boundary) => type switch
    {
        AttributeDataType.Text => CompareStrings(value.StringValue, criterion.StringValue, criterion.ComparisonType, criterion.CaseSensitive),
        AttributeDataType.Number => CompareOrdered(value.IntValue, criterion.IntValue, criterion.ComparisonType),
        AttributeDataType.LongNumber => CompareOrdered(value.LongValue, criterion.LongValue, criterion.ComparisonType),
        // Numeric and scale-insensitive: an actual of 5.00 equals a criterion of 5.0.
        AttributeDataType.Decimal => CompareOrdered(value.DecimalValue, criterion.DecimalValue, criterion.ComparisonType),
        AttributeDataType.DateTime => CompareOrdered(value.DateTimeValue, boundary, criterion.ComparisonType),
        AttributeDataType.Boolean => CompareEquality(value.BoolValue, criterion.BoolValue, criterion.ComparisonType),
        AttributeDataType.Guid => CompareEquality(value.GuidValue, criterion.GuidValue, criterion.ComparisonType),
        _ => false
    };

    private static bool CompareStrings(string? actual, string? expected, SearchComparisonType comparisonType, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return comparisonType switch
        {
            SearchComparisonType.Equals => string.Equals(actual, expected, comparison),
            SearchComparisonType.NotEquals => !string.Equals(actual, expected, comparison),
            SearchComparisonType.StartsWith => actual?.StartsWith(expected ?? "", comparison) ?? false,
            SearchComparisonType.NotStartsWith => !(actual?.StartsWith(expected ?? "", comparison) ?? false),
            SearchComparisonType.EndsWith => actual?.EndsWith(expected ?? "", comparison) ?? false,
            SearchComparisonType.NotEndsWith => !(actual?.EndsWith(expected ?? "", comparison) ?? false),
            SearchComparisonType.Contains => actual?.Contains(expected ?? "", comparison) ?? false,
            SearchComparisonType.NotContains => !(actual?.Contains(expected ?? "", comparison) ?? false),
            _ => false
        };
    }

    private static bool CompareOrdered<T>(T? actual, T? expected, SearchComparisonType comparisonType) where T : struct, IComparable<T>
    {
        if (!actual.HasValue || !expected.HasValue)
            return comparisonType == SearchComparisonType.Equals && actual.HasValue == expected.HasValue;

        var order = actual.GetValueOrDefault().CompareTo(expected.GetValueOrDefault());
        return comparisonType switch
        {
            SearchComparisonType.Equals => order == 0,
            SearchComparisonType.NotEquals => order != 0,
            SearchComparisonType.LessThan => order < 0,
            SearchComparisonType.LessThanOrEquals => order <= 0,
            SearchComparisonType.GreaterThan => order > 0,
            SearchComparisonType.GreaterThanOrEquals => order >= 0,
            _ => false
        };
    }

    private static bool CompareEquality<T>(T? actual, T? expected, SearchComparisonType comparisonType) where T : struct, IEquatable<T>
    {
        var equal = actual.HasValue
            ? expected.HasValue && actual.GetValueOrDefault().Equals(expected.GetValueOrDefault())
            : !expected.HasValue;
        return comparisonType switch
        {
            SearchComparisonType.Equals => equal,
            SearchComparisonType.NotEquals => !equal,
            _ => false
        };
    }
}
