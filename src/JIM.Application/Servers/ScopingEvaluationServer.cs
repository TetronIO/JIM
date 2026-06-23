// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// Evaluates scoping criteria for Synchronisation Rules.
/// Supports both export (MVO evaluation) and import (CSO evaluation) Synchronisation Rules.
/// </summary>
public class ScopingEvaluationServer
{
    public ScopingEvaluationServer()
    {
    }

    #region Export (MVO) Scoping

    /// <summary>
    /// Checks if an MVO is in scope for an export rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    public bool IsMvoInScopeForExportRule(MetaverseObject mvo, SyncRule exportRule, DateTime? nowUtc = null)
    {
        if (exportRule.Direction != SyncRuleDirection.Export)
        {
            Log.Warning("IsMvoInScopeForExportRule: Called with non-export rule {RuleName}", exportRule.Name);
            return false;
        }

        // No scoping criteria means all objects are in scope
        if (exportRule.ObjectScopingCriteriaGroups.Count == 0)
            return true;

        // Resolve "now" once per evaluation so all relative date criteria in this pass share a single boundary.
        var evaluationTime = nowUtc ?? DateTime.UtcNow;

        // Evaluate each criteria group (they are ORed together at the top level)
        foreach (var criteriaGroup in exportRule.ObjectScopingCriteriaGroups)
        {
            if (EvaluateMvoScopingCriteriaGroup(mvo, criteriaGroup, evaluationTime))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates a single scoping criteria group against an MVO.
    /// </summary>
    private bool EvaluateMvoScopingCriteriaGroup(MetaverseObject mvo, SyncRuleScopingCriteriaGroup group, DateTime nowUtc)
    {
        var criteriaResults = new List<bool>();

        // Evaluate individual criteria
        foreach (var criterion in group.Criteria)
        {
            criteriaResults.Add(EvaluateMvoScopingCriterion(mvo, criterion, nowUtc));
        }

        // Evaluate child groups recursively
        foreach (var childGroup in group.ChildGroups)
        {
            criteriaResults.Add(EvaluateMvoScopingCriteriaGroup(mvo, childGroup, nowUtc));
        }

        if (criteriaResults.Count == 0)
            return true; // Empty group is always true

        // Apply AND/OR logic based on group type
        return group.Type switch
        {
            SearchGroupType.All => criteriaResults.All(r => r),
            SearchGroupType.Any => criteriaResults.Any(r => r),
            _ => false
        };
    }

    /// <summary>
    /// Evaluates a single scoping criterion against an MVO attribute.
    /// </summary>
    private bool EvaluateMvoScopingCriterion(MetaverseObject mvo, SyncRuleScopingCriteria criterion, DateTime nowUtc)
    {
        if (criterion.MetaverseAttribute == null)
            return false;

        // Fail fast (and loudly) if the operator cannot apply to this attribute type. Such a criterion should
        // never have been persisted (the write path now rejects it), but if a legacy or externally-mutated rule
        // carries one, evaluating it would silently drop objects from scope; hard-fail so it is reported instead.
        EnsureOperatorValidForType(criterion, criterion.MetaverseAttribute.Type, criterion.MetaverseAttribute.Name);

        // Get the MVO attribute value
        var mvoAttributeValue = mvo.AttributeValues
            .FirstOrDefault(av => av.AttributeId == criterion.MetaverseAttribute.Id);

        // Handle null/missing attribute values
        if (mvoAttributeValue == null)
        {
            // Only Equals against an all-null absolute criterion should match a missing value.
            // A relative date criterion always resolves to a real boundary, so it never matches a missing value here.
            return criterion.ComparisonType == SearchComparisonType.Equals &&
                   criterion.ValueMode == DateCriteriaValueMode.Absolute &&
                   criterion.StringValue == null &&
                   criterion.IntValue == null &&
                   criterion.LongValue == null &&
                   criterion.DateTimeValue == null &&
                   criterion.BoolValue == null &&
                   criterion.GuidValue == null;
        }

        // Evaluate based on attribute type
        return criterion.MetaverseAttribute.Type switch
        {
            AttributeDataType.Text => EvaluateStringComparison(mvoAttributeValue.StringValue, criterion.StringValue, criterion.ComparisonType, criterion.CaseSensitive),
            AttributeDataType.Number => EvaluateNumberComparison(mvoAttributeValue.IntValue, criterion.IntValue, criterion.ComparisonType),
            AttributeDataType.LongNumber => EvaluateLongNumberComparison(mvoAttributeValue.LongValue, criterion.LongValue, criterion.ComparisonType),
            AttributeDataType.DateTime => EvaluateDateTimeComparison(mvoAttributeValue.DateTimeValue, ResolveCriterionDate(criterion, nowUtc), criterion.ComparisonType),
            AttributeDataType.Boolean => EvaluateBooleanComparison(mvoAttributeValue.BoolValue, criterion.BoolValue, criterion.ComparisonType),
            AttributeDataType.Guid => EvaluateGuidComparison(mvoAttributeValue.GuidValue, criterion.GuidValue, criterion.ComparisonType),
            _ => false
        };
    }

    #endregion

    #region Import (CSO) Scoping

    /// <summary>
    /// Checks if a CSO is in scope for an import rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    public bool IsCsoInScopeForImportRule(ConnectedSystemObject cso, SyncRule importRule, DateTime? nowUtc = null)
    {
        if (importRule.Direction != SyncRuleDirection.Import)
        {
            Log.Warning("IsCsoInScopeForImportRule: Called with non-import rule {RuleName}", importRule.Name);
            return false;
        }

        // No scoping criteria means all objects are in scope
        if (importRule.ObjectScopingCriteriaGroups.Count == 0)
            return true;

        // Resolve "now" once per evaluation so all relative date criteria in this pass share a single boundary.
        var evaluationTime = nowUtc ?? DateTime.UtcNow;

        // Evaluate each criteria group (they are ORed together at the top level)
        foreach (var criteriaGroup in importRule.ObjectScopingCriteriaGroups)
        {
            if (EvaluateCsoScopingCriteriaGroup(cso, criteriaGroup, evaluationTime))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates a single scoping criteria group against a CSO.
    /// </summary>
    private bool EvaluateCsoScopingCriteriaGroup(ConnectedSystemObject cso, SyncRuleScopingCriteriaGroup group, DateTime nowUtc)
    {
        var criteriaResults = new List<bool>();

        // Evaluate individual criteria
        foreach (var criterion in group.Criteria)
        {
            criteriaResults.Add(EvaluateCsoScopingCriterion(cso, criterion, nowUtc));
        }

        // Evaluate child groups recursively
        foreach (var childGroup in group.ChildGroups)
        {
            criteriaResults.Add(EvaluateCsoScopingCriteriaGroup(cso, childGroup, nowUtc));
        }

        if (criteriaResults.Count == 0)
            return true; // Empty group is always true

        // Apply AND/OR logic based on group type
        return group.Type switch
        {
            SearchGroupType.All => criteriaResults.All(r => r),
            SearchGroupType.Any => criteriaResults.Any(r => r),
            _ => false
        };
    }

    /// <summary>
    /// Evaluates a single scoping criterion against a CSO attribute.
    /// </summary>
    private bool EvaluateCsoScopingCriterion(ConnectedSystemObject cso, SyncRuleScopingCriteria criterion, DateTime nowUtc)
    {
        if (criterion.ConnectedSystemAttribute == null)
            return false;

        // Fail fast (and loudly) if the operator cannot apply to this attribute type. Such a criterion should
        // never have been persisted (the write path now rejects it), but if a legacy or externally-mutated rule
        // carries one, evaluating it would silently drop objects from scope; hard-fail so it is reported instead.
        EnsureOperatorValidForType(criterion, criterion.ConnectedSystemAttribute.Type, criterion.ConnectedSystemAttribute.Name);

        // Get the CSO attribute value
        var csoAttributeValue = cso.AttributeValues
            .FirstOrDefault(av => av.AttributeId == criterion.ConnectedSystemAttribute.Id);

        // Handle null/missing attribute values
        if (csoAttributeValue == null)
        {
            // Only Equals against an all-null absolute criterion should match a missing value.
            // A relative date criterion always resolves to a real boundary, so it never matches a missing value here.
            return criterion.ComparisonType == SearchComparisonType.Equals &&
                   criterion.ValueMode == DateCriteriaValueMode.Absolute &&
                   criterion.StringValue == null &&
                   criterion.IntValue == null &&
                   criterion.LongValue == null &&
                   criterion.DateTimeValue == null &&
                   criterion.BoolValue == null &&
                   criterion.GuidValue == null;
        }

        // Evaluate based on attribute type
        return criterion.ConnectedSystemAttribute.Type switch
        {
            AttributeDataType.Text => EvaluateStringComparison(csoAttributeValue.StringValue, criterion.StringValue, criterion.ComparisonType, criterion.CaseSensitive),
            AttributeDataType.Number => EvaluateNumberComparison(csoAttributeValue.IntValue, criterion.IntValue, criterion.ComparisonType),
            AttributeDataType.LongNumber => EvaluateLongNumberComparison(csoAttributeValue.LongValue, criterion.LongValue, criterion.ComparisonType),
            AttributeDataType.DateTime => EvaluateDateTimeComparison(csoAttributeValue.DateTimeValue, ResolveCriterionDate(criterion, nowUtc), criterion.ComparisonType),
            AttributeDataType.Boolean => EvaluateBooleanComparison(csoAttributeValue.BoolValue, criterion.BoolValue, criterion.ComparisonType),
            AttributeDataType.Guid => EvaluateGuidComparison(csoAttributeValue.GuidValue, criterion.GuidValue, criterion.ComparisonType),
            _ => false
        };
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the criterion's comparison operator is not applicable
    /// to the attribute's data type (per the shared <see cref="SearchComparisonOperators"/> rule), after logging
    /// the misconfiguration. Defence in depth: the write path rejects such combinations, so reaching here means a
    /// rule was persisted before that guard existed or was mutated outside it; failing loudly surfaces the problem
    /// rather than silently mis-scoping objects.
    /// </summary>
    private static void EnsureOperatorValidForType(SyncRuleScopingCriteria criterion, AttributeDataType type, string attributeName)
    {
        if (SearchComparisonOperators.IsValid(criterion.ComparisonType, type))
            return;

        Log.Error("Scoping evaluation: comparison operator {Operator} is not valid for the {Type} attribute {Attribute}; " +
                  "the Synchronisation Rule is misconfigured", criterion.ComparisonType, type, attributeName);
        throw new InvalidOperationException(
            $"Comparison operator '{criterion.ComparisonType}' is not valid for the {type} attribute '{attributeName}' on scoping criteria.");
    }

    /// <summary>
    /// Resolves the effective DateTime boundary for a criterion: the relative boundary (resolved against
    /// <paramref name="nowUtc"/>) when the criterion is Relative, otherwise its fixed <see cref="SyncRuleScopingCriteria.DateTimeValue"/>.
    /// </summary>
    private static DateTime? ResolveCriterionDate(SyncRuleScopingCriteria criterion, DateTime nowUtc)
    {
        if (criterion.ValueMode == DateCriteriaValueMode.Relative &&
            criterion.RelativeCount.HasValue && criterion.RelativeUnit.HasValue && criterion.RelativeDirection.HasValue)
        {
            return RelativeDateResolver.Resolve(criterion.RelativeCount.Value, criterion.RelativeUnit.Value, criterion.RelativeDirection.Value, nowUtc);
        }

        return criterion.DateTimeValue;
    }

    #endregion

    #region Comparison Helpers

    private bool EvaluateStringComparison(string? actual, string? expected, SearchComparisonType comparisonType, bool caseSensitive = true)
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

    private bool EvaluateNumberComparison(int? actual, int? expected, SearchComparisonType comparisonType)
    {
        if (!actual.HasValue || !expected.HasValue)
            return comparisonType == SearchComparisonType.Equals && actual == expected;

        return comparisonType switch
        {
            SearchComparisonType.Equals => actual.Value == expected.Value,
            SearchComparisonType.NotEquals => actual.Value != expected.Value,
            SearchComparisonType.LessThan => actual.Value < expected.Value,
            SearchComparisonType.LessThanOrEquals => actual.Value <= expected.Value,
            SearchComparisonType.GreaterThan => actual.Value > expected.Value,
            SearchComparisonType.GreaterThanOrEquals => actual.Value >= expected.Value,
            _ => false
        };
    }

    private bool EvaluateLongNumberComparison(long? actual, long? expected, SearchComparisonType comparisonType)
    {
        if (!actual.HasValue || !expected.HasValue)
            return comparisonType == SearchComparisonType.Equals && actual == expected;

        return comparisonType switch
        {
            SearchComparisonType.Equals => actual.Value == expected.Value,
            SearchComparisonType.NotEquals => actual.Value != expected.Value,
            SearchComparisonType.LessThan => actual.Value < expected.Value,
            SearchComparisonType.LessThanOrEquals => actual.Value <= expected.Value,
            SearchComparisonType.GreaterThan => actual.Value > expected.Value,
            SearchComparisonType.GreaterThanOrEquals => actual.Value >= expected.Value,
            _ => false
        };
    }

    private bool EvaluateDateTimeComparison(DateTime? actual, DateTime? expected, SearchComparisonType comparisonType)
    {
        if (!actual.HasValue || !expected.HasValue)
            return comparisonType == SearchComparisonType.Equals && actual == expected;

        return comparisonType switch
        {
            SearchComparisonType.Equals => actual.Value == expected.Value,
            SearchComparisonType.NotEquals => actual.Value != expected.Value,
            SearchComparisonType.LessThan => actual.Value < expected.Value,
            SearchComparisonType.LessThanOrEquals => actual.Value <= expected.Value,
            SearchComparisonType.GreaterThan => actual.Value > expected.Value,
            SearchComparisonType.GreaterThanOrEquals => actual.Value >= expected.Value,
            _ => false
        };
    }

    private bool EvaluateBooleanComparison(bool? actual, bool? expected, SearchComparisonType comparisonType)
    {
        return comparisonType switch
        {
            SearchComparisonType.Equals => actual == expected,
            SearchComparisonType.NotEquals => actual != expected,
            _ => false
        };
    }

    private bool EvaluateGuidComparison(Guid? actual, Guid? expected, SearchComparisonType comparisonType)
    {
        return comparisonType switch
        {
            SearchComparisonType.Equals => actual == expected,
            SearchComparisonType.NotEquals => actual != expected,
            _ => false
        };
    }

    #endregion
}
