// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

public class SyncRuleScopingCriteria : ICriterionValues
{
    public int Id { get; set; }

    /// <summary>
    /// The Metaverse Attribute to evaluate for outbound (export) Synchronisation Rules.
    /// One of MetaverseAttribute or ConnectedSystemAttribute must be set.
    /// </summary>
    public MetaverseAttribute? MetaverseAttribute { get; set; }
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The Connected System Object Type Attribute to evaluate for inbound (import) Synchronisation Rules.
    /// One of MetaverseAttribute or ConnectedSystemAttribute must be set.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// An expression whose computed value the criterion compares, in place of an attribute (#2054). It reads
    /// <c>cs["..."]</c> on an import rule and <c>mv["..."]</c> on an export rule. Null for an attribute criterion.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// The type an expression criterion's computed value is compared as, which decides the operators it may use and
    /// how its value is read. Chosen by the administrator rather than inferred, since an expression can compute no value
    /// for the object it is tried against. Null for an attribute criterion, whose attribute's own type applies.
    /// </summary>
    public AttributeDataType? CompareAs { get; set; }

    public SearchComparisonType ComparisonType { get; set; } = SearchComparisonType.NotSet;

    public string? StringValue { get; set; }

    public int? IntValue { get; set; }

    public long? LongValue { get; set; }

    public decimal? DecimalValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public bool? BoolValue { get; set; }

    public Guid? GuidValue { get; set; }

    /// <summary>
    /// When true (default), value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// Only applies to text/string comparisons.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// For DateTime attributes, whether this criterion compares against a fixed (Absolute) date held in
    /// <see cref="DateTimeValue"/>, or a date resolved Relative to "now" at each evaluation. Defaults to Absolute.
    /// </summary>
    public DateCriteriaValueMode ValueMode { get; set; } = DateCriteriaValueMode.Absolute;

    /// <summary>
    /// The number of units to offset from "now" when <see cref="ValueMode"/> is Relative. Zero or positive.
    /// </summary>
    public int? RelativeCount { get; set; }

    /// <summary>
    /// The unit of the relative offset (Hours, Days, Weeks, Months, Years) when <see cref="ValueMode"/> is Relative.
    /// </summary>
    public RelativeDateUnit? RelativeUnit { get; set; }

    /// <summary>
    /// The direction of the relative offset (Ago or FromNow) when <see cref="ValueMode"/> is Relative.
    /// </summary>
    public RelativeDateDirection? RelativeDirection { get; set; }

    /// <summary>
    /// Whether the criterion compares an expression's computed value rather than an attribute.
    /// </summary>
    public bool IsExpression => !string.IsNullOrWhiteSpace(Expression);

    /// <summary>
    /// Whether Temporal Scope Reconciliation must evaluate every object of the scoped type for this criterion: it
    /// compares an expression's computed date against a relative date, so no stored value can narrow the objects whose
    /// boundary the clock has crossed.
    /// </summary>
    public bool NeedsFullReconciliationPass =>
        IsExpression && CompareAs == AttributeDataType.DateTime && ValueMode == DateCriteriaValueMode.Relative;

    /// <summary>
    /// What saving a criterion that <see cref="NeedsFullReconciliationPass"/> tells the administrator, word for word
    /// on every surface.
    /// </summary>
    public const string FullReconciliationPassNotice =
        "This criterion compares an expression against a relative date. Temporal Scope Reconciliation can only narrow a " +
        "relative date to the objects whose date crossed it when the date is stored in an attribute, so for this rule " +
        "every reconciliation evaluates every object of its type, in batches. On a large object type, prefer flowing the " +
        "date into an attribute and comparing that.";

    /// <summary>
    /// Whether the criterion has exactly one left-hand side: an attribute (either side), or an expression with a type to
    /// compare it as. An attribute criterion takes its type from the attribute, so it carries no compare-as type.
    /// </summary>
    public bool IsValid()
    {
        var hasAttribute = MetaverseAttributeId.HasValue || MetaverseAttribute != null ||
                           ConnectedSystemAttributeId.HasValue || ConnectedSystemAttribute != null;

        return IsExpression
            ? !hasAttribute && CompareAs.HasValue
            : hasAttribute && !CompareAs.HasValue;
    }

    /// <summary>
    /// Gets the data type the criterion compares: the type an expression is compared as, or the attribute's own type.
    /// </summary>
    public AttributeDataType? GetAttributeDataType()
    {
        if (IsExpression)
            return CompareAs;
        if (MetaverseAttribute != null)
            return MetaverseAttribute.Type;
        if (ConnectedSystemAttribute != null)
            return ConnectedSystemAttribute.Type;
        return null;
    }

    /// <summary>
    /// Gets what the criterion compares, for display: the expression, or the attribute's name.
    /// </summary>
    public string? GetAttributeName()
    {
        if (IsExpression)
            return Expression;
        if (MetaverseAttribute != null)
            return MetaverseAttribute.Name;
        if (ConnectedSystemAttribute != null)
            return ConnectedSystemAttribute.Name;
        return null;
    }

    public override string ToString()
    {
        var attributeType = GetAttributeDataType();
        if (attributeType == null)
            return "No attribute set";

        return attributeType switch
        {
            AttributeDataType.Text => "Text: " + StringValue,
            AttributeDataType.Number => "Number: " + IntValue,
            AttributeDataType.LongNumber => "LongNumber: " + LongValue,
            AttributeDataType.Decimal => "Decimal: " + (DecimalValue.HasValue ? DecimalValue.Value.ToString(CultureInfo.InvariantCulture) : null),
            AttributeDataType.Boolean => "Boolean: " + (BoolValue is null ? "Null" : BoolValue.Value.ToString()),
            AttributeDataType.DateTime => DescribeDateValue(),
            AttributeDataType.Guid => "Guid: " + GuidValue,
            _ => "Unsupported data type"
        };
    }

    /// <summary>
    /// Renders the DateTime value for display: a relative phrase ("30 days ago") when Relative, otherwise the absolute date.
    /// </summary>
    private string DescribeDateValue()
    {
        if (ValueMode == DateCriteriaValueMode.Relative && RelativeCount.HasValue && RelativeUnit.HasValue && RelativeDirection.HasValue)
            return RelativeDateResolver.Describe(RelativeCount.Value, RelativeUnit.Value, RelativeDirection.Value);

        return "Date: " + DateTimeValue;
    }
}