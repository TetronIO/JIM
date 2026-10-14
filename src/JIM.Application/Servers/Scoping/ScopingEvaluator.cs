// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
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
/// <item><description>Every comparison means what <see cref="CriterionComparer"/> says it means (#2053): a positive operator is met by any value, a negated one only when the attribute holds a value and none matches what it negates (#1923), and a missing value fails every comparison except "has no value".</description></item>
/// <item><description>Relative dates resolve against one instant per evaluation.</description></item>
/// <item><description>An operator invalid for the attribute's type, or a pattern that is invalid or runs past its time limit, makes synchronisation fail loudly when it is reached.</description></item>
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
    /// Whether the object is in scope of the rule, exactly as synchronisation decides it. Throws
    /// <see cref="InvalidOperationException"/> on reaching an invalid criterion, and
    /// <see cref="ScopingCriterionFailedException"/> when a pattern runs past its time limit on one of the object's
    /// values (#2053), so the caller can stop that object rather than decide its scope on a guess.
    /// </summary>
    internal static bool IsInScope<TSource>(SyncRule rule, TSource source, DateTime nowUtc, Lazy<IExpressionEvaluator> expressions)
        where TSource : struct, IScopingValueSource
    {
        var topLevelGroups = rule.ObjectScopingCriteriaGroups;
        for (var i = 0; i < topLevelGroups.Count; i++)
        {
            if (EvaluateGroup(topLevelGroups[i], source, nowUtc, node: null, rule.Name, expressions) == true)
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
        List<ScopingGroupExplanation> groupNodes, Lazy<IExpressionEvaluator> expressions)
        where TSource : struct, IScopingValueSource
    {
        ScopingRuleOutcome? decided = null;
        for (var i = 0; i < topLevelGroups.Count; i++)
        {
            var node = ScopingTraceBuilder.CreateGroup(topLevelGroups[i], (i + 1).ToString(CultureInfo.InvariantCulture));
            groupNodes.Add(node);

            // Synchronisation stops at the first group that is met, or fails at the first that reaches an invalid
            // criterion; whichever comes first decides. Later groups are still evaluated, for the explanation only.
            var met = EvaluateGroup(topLevelGroups[i], source, nowUtc, node, ruleName: null, expressions);
            if (decided == null && met != false)
                decided = met == true ? ScopingRuleOutcome.InScope : ScopingRuleOutcome.Undetermined;
        }

        return decided ?? ScopingRuleOutcome.OutOfScope;
    }

    /// <summary>
    /// Evaluates a group and its descendants. Null only when tracing and the group holds an invalid criterion at any
    /// depth (synchronisation would have thrown on reaching it).
    /// </summary>
    private static bool? EvaluateGroup<TSource>(SyncRuleScopingCriteriaGroup group, TSource source, DateTime nowUtc, ScopingGroupExplanation? node,
        string? ruleName, Lazy<IExpressionEvaluator> expressions)
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
            var outcome = criteria[i].IsExpression
                ? EvaluateExpressionCriterion(criteria[i], source, nowUtc, criterionNode, ruleName, expressions.Value)
                : EvaluateCriterion(criteria[i], source, nowUtc, criterionNode, ruleName);
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
            var met = EvaluateGroup(childGroups[i], source, nowUtc, childNode, ruleName, expressions);
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
        ScopingCriterionExplanation? node, string? ruleName)
        where TSource : struct, IScopingValueSource
    {
        if (!source.TryGetAttribute(criterion, out var attributeId, out var type, out var attributeName))
            return ScopingCriterionOutcome.AttributeMissing;

        var invalidReason = InvalidReason(criterion, type);
        if (invalidReason != null)
        {
            if (node == null)
                throw InvalidCriterion(invalidReason, criterion.ComparisonType, type, attributeName);

            ScopingTraceBuilder.RecordAttribute(node, criterion, attributeId, type, attributeName, boundary: null, value: null, valueCount: 0);
            return ScopingCriterionOutcome.Invalid;
        }

        var boundary = type == AttributeDataType.DateTime ? ResolveCriterionDate(criterion, nowUtc) : null;
        var expected = new ScopingValue(criterion.StringValue, criterion.IntValue, criterion.LongValue, criterion.DecimalValue,
            boundary, criterion.BoolValue, criterion.GuidValue);

        var values = new SourceValueCursor<TSource>(source, attributeId);
        CriterionVerdict verdict;
        try
        {
            verdict = CriterionComparer.Evaluate(ref values, type, criterion.ComparisonType, expected, criterion.CaseSensitive);
        }
        catch (RegexMatchTimeoutException ex) when (node == null)
        {
            // Synchronisation cannot tell whether the object is in scope, so it stops the object rather than guess.
            Log.Warning(ex, "Scoping evaluation: the pattern on {Attribute} in {SyncRule} ran past its time limit",
                LogSanitiser.Sanitise(attributeName), LogSanitiser.Sanitise(ruleName));
            throw new ScopingCriterionFailedException(ruleName ?? "(unnamed)", attributeName,
                $"Its pattern '{criterion.StringValue}' ran past its time limit of {CriterionPatterns.MatchTimeout.TotalMilliseconds:0} ms " +
                "on one of the object's values; simplify the pattern (nested repetition such as (a+)+ is the usual cause).", ex);
        }
        catch (RegexMatchTimeoutException ex) when (node != null)
        {
            // Synchronisation fails the object on a pattern that runs past its time limit; an explanation shows the
            // criterion as one that cannot be evaluated, as it would an invalid one, so the rest of the tree still shows.
            Log.Warning(ex, "Scoping explanation: the pattern on {Attribute} ran past its time limit", LogSanitiser.Sanitise(attributeName));
            ScopingTraceBuilder.RecordAttribute(node, criterion, attributeId, type, attributeName, boundary, value: null, valueCount: 0);
            return ScopingCriterionOutcome.Invalid;
        }

        var outcome = verdict.Met
            ? ScopingCriterionOutcome.Met
            : verdict.HeldCount > 0 ? ScopingCriterionOutcome.NotMet : ScopingCriterionOutcome.NoValue;

        if (node != null)
        {
            // The value the outcome turned on: the one that decided it, or a lone value either way. Where several values
            // all went the same way, no single one did, and none is named.
            var valueCount = source.CountValues(attributeId);
            ScopingValue? shown = verdict.Decided || valueCount == 1 ? verdict.LastHeld : null;
            ScopingTraceBuilder.RecordAttribute(node, criterion, attributeId, type, attributeName, boundary, shown, valueCount);
        }

        return outcome;
    }

    /// <summary>
    /// A criterion comparing an expression's computed value (#2054): the expression is evaluated against the object, each
    /// value it computes is read as the criterion's compare-as type, and the values are compared exactly as an attribute's
    /// are. An expression that fails to evaluate, or computes a value that is not of that type, makes the object's scope
    /// undecidable: synchronisation stops the object (<see cref="ScopingCriterionFailedException"/>), and an explanation
    /// shows the criterion as one that cannot be evaluated.
    /// </summary>
    private static ScopingCriterionOutcome EvaluateExpressionCriterion<TSource>(SyncRuleScopingCriteria criterion, TSource source,
        DateTime nowUtc, ScopingCriterionExplanation? node, string? ruleName, IExpressionEvaluator expressions)
        where TSource : struct, IScopingValueSource
    {
        var expression = criterion.Expression!;
        var type = criterion.CompareAs ?? AttributeDataType.NotSet;

        var invalidReason = type == AttributeDataType.NotSet
            ? "it has no type to compare the expression's value as"
            : InvalidReason(criterion, type);
        if (invalidReason != null)
        {
            if (node == null)
                throw InvalidCriterion(invalidReason, criterion.ComparisonType, type, expression);

            ScopingTraceBuilder.RecordAttribute(node, criterion, attributeId: 0, type, expression, boundary: null, value: null, valueCount: 0);
            return ScopingCriterionOutcome.Invalid;
        }

        var boundary = type == AttributeDataType.DateTime ? ResolveCriterionDate(criterion, nowUtc) : null;
        var expected = new ScopingValue(criterion.StringValue, criterion.IntValue, criterion.LongValue, criterion.DecimalValue,
            boundary, criterion.BoolValue, criterion.GuidValue);

        List<ScopingValue> computed;
        string? failure;
        try
        {
            var result = expressions.Evaluate(expression, source.ExpressionContext(nowUtc));
            failure = TryReadComputedValues(result, type, out computed);
        }
        catch (ExpressionEvaluationException ex)
        {
            computed = [];
            failure = $"Its expression could not be evaluated: {ex.Message}";
        }

        CriterionVerdict verdict = default;
        if (failure == null)
        {
            var values = new ComputedValueCursor(computed);
            try
            {
                verdict = CriterionComparer.Evaluate(ref values, type, criterion.ComparisonType, expected, criterion.CaseSensitive);
            }
            catch (RegexMatchTimeoutException)
            {
                failure = $"Its pattern '{criterion.StringValue}' ran past its time limit of {CriterionPatterns.MatchTimeout.TotalMilliseconds:0} ms " +
                          "on one of the expression's values; simplify the pattern (nested repetition such as (a+)+ is the usual cause).";
            }
        }

        if (failure != null)
        {
            if (node == null)
            {
                Log.Warning("Scoping evaluation: the expression criterion {Expression} in {SyncRule} could not be evaluated: {Reason}",
                    LogSanitiser.Sanitise(expression), LogSanitiser.Sanitise(ruleName), LogSanitiser.Sanitise(failure));
                throw new ScopingCriterionFailedException(ruleName ?? "(unnamed)", expression, failure);
            }

            ScopingTraceBuilder.RecordAttribute(node, criterion, attributeId: 0, type, expression, boundary, value: null, valueCount: 0);
            return ScopingCriterionOutcome.Invalid;
        }

        if (node != null)
        {
            ScopingValue? shown = verdict.Decided || computed.Count == 1 ? verdict.LastHeld : null;
            ScopingTraceBuilder.RecordAttribute(node, criterion, attributeId: 0, type, expression, boundary, shown, computed.Count);
        }

        return verdict.Met
            ? ScopingCriterionOutcome.Met
            : verdict.HeldCount > 0 ? ScopingCriterionOutcome.NotMet : ScopingCriterionOutcome.NoValue;
    }

    /// <summary>
    /// Reads every value an expression computed as <paramref name="type"/>, the way an expression's result is read
    /// everywhere else: each element of a collection, with no value and blank text read as no value. Returns why one
    /// cannot be read, naming it, or null when all can.
    /// </summary>
    private static string? TryReadComputedValues(object? result, AttributeDataType type, out List<ScopingValue> values)
    {
        values = [];
        foreach (var element in ExpressionResultValues.Elements(result))
        {
            if (element == null || element is string text && string.IsNullOrWhiteSpace(text))
                continue;

            if (!ExpressionResultConverter.TryConvert(element, type, out var converted) || converted == null)
                return $"Its expression computed '{ExpressionText.Describe(element)}', which cannot be compared as {DescribeType(type)}.";

            values.Add(converted switch
            {
                string s => new ScopingValue(s, null, null, null, null, null, null),
                int i => new ScopingValue(null, i, null, null, null, null, null),
                long l => new ScopingValue(null, null, l, null, null, null, null),
                decimal d => new ScopingValue(null, null, null, d, null, null, null),
                DateTime dt => new ScopingValue(null, null, null, null, dt, null, null),
                bool b => new ScopingValue(null, null, null, null, null, b, null),
                Guid g => new ScopingValue(null, null, null, null, null, null, g),
                _ => new ScopingValue(converted.ToString(), null, null, null, null, null, null)
            });
        }

        return null;
    }

    private static string DescribeType(AttributeDataType type) => type switch
    {
        AttributeDataType.Number => "a Number",
        AttributeDataType.LongNumber => "a Long Number",
        AttributeDataType.Decimal => "a Decimal",
        AttributeDataType.DateTime => "a DateTime",
        AttributeDataType.Boolean => "a Boolean",
        AttributeDataType.Guid => "a Guid",
        _ => type.ToString()
    };

    /// <summary>An expression's computed values, walked as an attribute's are.</summary>
    private struct ComputedValueCursor(List<ScopingValue> values) : IScopingValueCursor
    {
        private int _position;

        public bool TryGetNext(out ScopingValue value)
        {
            if (_position < values.Count)
            {
                value = values[_position++];
                return true;
            }

            value = default;
            return false;
        }
    }

    /// <summary>
    /// Why a criterion cannot be evaluated against an attribute of this type, or null when it can: its operator does
    /// not apply to the type in scoping, or its pattern is not one JIM would save. Defence in depth: the write path
    /// refuses both, so reaching one here means a rule was persisted before that guard existed or was changed outside
    /// it, and silently mis-scoping objects would be worse than failing.
    /// </summary>
    private static string? InvalidReason(SyncRuleScopingCriteria criterion, AttributeDataType type)
    {
        if (!SearchComparisonOperators.IsValid(criterion.ComparisonType, type, CriterionContext.Scoping))
            return $"Comparison operator '{criterion.ComparisonType}' is not valid for the {type} attribute";

        return SearchComparisonOperators.IsPattern(criterion.ComparisonType)
            ? SearchComparisonOperators.ValidatePattern(criterion.StringValue)
            : null;
    }

    /// <summary>The failure for a criterion that cannot be evaluated, logged as it is created.</summary>
    private static InvalidOperationException InvalidCriterion(string reason, SearchComparisonType comparisonType, AttributeDataType type, string attributeName)
    {
        Log.Error("Scoping evaluation: a {Operator} criterion on the {Type} attribute {Attribute} cannot be evaluated ({Reason}); " +
                  "the Synchronisation Rule is misconfigured", comparisonType, type, attributeName, reason);
        return new InvalidOperationException(
            $"The '{comparisonType}' scoping criterion on the {type} attribute '{attributeName}' cannot be evaluated: {reason}.");
    }

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

    /// <summary>One attribute's values on a scoping source, walked without allocating.</summary>
    private struct SourceValueCursor<TSource> : IScopingValueCursor
        where TSource : struct, IScopingValueSource
    {
        private readonly TSource _source;
        private readonly int _attributeId;
        private int _position;

        public SourceValueCursor(TSource source, int attributeId)
        {
            _source = source;
            _attributeId = attributeId;
            _position = 0;
        }

        public bool TryGetNext(out ScopingValue value) => _source.TryGetNextValue(_attributeId, ref _position, out value);
    }
}
