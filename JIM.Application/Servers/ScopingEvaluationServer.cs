using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// Evaluates scoping criteria for sync rules.
/// Supports both export (MVO evaluation) and import (CSO evaluation) sync rules.
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
    public bool IsMvoInScopeForExportRule(MetaverseObject mvo, SyncRule exportRule)
    {
        if (exportRule.Direction != SyncRuleDirection.Export)
        {
            Log.Warning("IsMvoInScopeForExportRule: Called with non-export rule {RuleName}", exportRule.Name);
            return false;
        }

        // No scoping criteria means all objects are in scope
        if (exportRule.ObjectScopingCriteriaGroups.Count == 0)
            return true;

        // Evaluate each criteria group (they are ORed together at the top level)
        foreach (var criteriaGroup in exportRule.ObjectScopingCriteriaGroups)
        {
            if (EvaluateMvoScopingCriteriaGroup(mvo, criteriaGroup))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates a single scoping criteria group against an MVO.
    /// </summary>
    private bool EvaluateMvoScopingCriteriaGroup(MetaverseObject mvo, SyncRuleScopingCriteriaGroup group)
    {
        var criteriaResults = new List<bool>();

        // Evaluate individual criteria
        foreach (var criterion in group.Criteria)
        {
            criteriaResults.Add(EvaluateMvoScopingCriterion(mvo, criterion));
        }

        // Evaluate child groups recursively
        foreach (var childGroup in group.ChildGroups)
        {
            criteriaResults.Add(EvaluateMvoScopingCriteriaGroup(mvo, childGroup));
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
    private bool EvaluateMvoScopingCriterion(MetaverseObject mvo, SyncRuleScopingCriteria criterion)
    {
        if (criterion.MetaverseAttribute == null)
            return false;

        // Get the MVO attribute value
        var mvoAttributeValue = mvo.AttributeValues
            .FirstOrDefault(av => av.AttributeId == criterion.MetaverseAttribute.Id);

        // Handle null/missing attribute values
        if (mvoAttributeValue == null)
        {
            // Only Equals with null value should match
            return criterion.ComparisonType == SearchComparisonType.Equals &&
                   criterion.StringValue == null &&
                   criterion.IntValue == null &&
                   criterion.DateTimeValue == null &&
                   criterion.BoolValue == null &&
                   criterion.GuidValue == null;
        }

        // Evaluate based on attribute type
        return criterion.MetaverseAttribute.Type switch
        {
            AttributeDataType.Text => EvaluateStringComparison(mvoAttributeValue.StringValue, criterion.StringValue, criterion.ComparisonType, criterion.CaseSensitive),
            AttributeDataType.Number => EvaluateNumberComparison(mvoAttributeValue.IntValue, criterion.IntValue, criterion.ComparisonType),
            AttributeDataType.DateTime => EvaluateDateTimeComparison(mvoAttributeValue.DateTimeValue, criterion.DateTimeValue, criterion.ComparisonType),
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
    public bool IsCsoInScopeForImportRule(ConnectedSystemObject cso, SyncRule importRule)
    {
        if (importRule.Direction != SyncRuleDirection.Import)
        {
            Log.Warning("IsCsoInScopeForImportRule: Called with non-import rule {RuleName}", importRule.Name);
            return false;
        }

        // No scoping criteria means all objects are in scope
        if (importRule.ObjectScopingCriteriaGroups.Count == 0)
            return true;

        // Evaluate each criteria group (they are ORed together at the top level)
        foreach (var criteriaGroup in importRule.ObjectScopingCriteriaGroups)
        {
            if (EvaluateCsoScopingCriteriaGroup(cso, criteriaGroup))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates a single scoping criteria group against a CSO.
    /// </summary>
    private bool EvaluateCsoScopingCriteriaGroup(ConnectedSystemObject cso, SyncRuleScopingCriteriaGroup group)
    {
        var criteriaResults = new List<bool>();

        // Evaluate individual criteria
        foreach (var criterion in group.Criteria)
        {
            criteriaResults.Add(EvaluateCsoScopingCriterion(cso, criterion));
        }

        // Evaluate child groups recursively
        foreach (var childGroup in group.ChildGroups)
        {
            criteriaResults.Add(EvaluateCsoScopingCriteriaGroup(cso, childGroup));
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
    private bool EvaluateCsoScopingCriterion(ConnectedSystemObject cso, SyncRuleScopingCriteria criterion)
    {
        if (criterion.ConnectedSystemAttribute == null)
            return false;

        // Get the CSO attribute value
        var csoAttributeValue = cso.AttributeValues
            .FirstOrDefault(av => av.AttributeId == criterion.ConnectedSystemAttribute.Id);

        // Handle null/missing attribute values
        if (csoAttributeValue == null)
        {
            // Only Equals with null value should match
            return criterion.ComparisonType == SearchComparisonType.Equals &&
                   criterion.StringValue == null &&
                   criterion.IntValue == null &&
                   criterion.DateTimeValue == null &&
                   criterion.BoolValue == null &&
                   criterion.GuidValue == null;
        }

        // Evaluate based on attribute type
        return criterion.ConnectedSystemAttribute.Type switch
        {
            AttributeDataType.Text => EvaluateStringComparison(csoAttributeValue.StringValue, criterion.StringValue, criterion.ComparisonType, criterion.CaseSensitive),
            AttributeDataType.Number => EvaluateNumberComparison(csoAttributeValue.IntValue, criterion.IntValue, criterion.ComparisonType),
            AttributeDataType.DateTime => EvaluateDateTimeComparison(csoAttributeValue.DateTimeValue, criterion.DateTimeValue, criterion.ComparisonType),
            AttributeDataType.Boolean => EvaluateBooleanComparison(csoAttributeValue.BoolValue, criterion.BoolValue, criterion.ComparisonType),
            AttributeDataType.Guid => EvaluateGuidComparison(csoAttributeValue.GuidValue, criterion.GuidValue, criterion.ComparisonType),
            _ => false
        };
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
