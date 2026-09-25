// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.Logic;

/// <summary>
/// The value extracted from a Metaverse Object for an Object Matching Rule's export-matching query
/// (MVO -&gt; CSO lookup), or the reason no value could be extracted. <see cref="Resolve"/> reproduces
/// exactly the value-extraction logic that the per-object export-matching query uses before it queries
/// the connector space, so the per-object query and the page-scoped batch candidate query share one
/// implementation of what counts as "the Metaverse Object's value" for a rule.
/// </summary>
public sealed class ExportMatchingValue
{
    /// <summary>
    /// Whether a value was resolved, or the reason none could be.
    /// </summary>
    public ExportMatchingOutcome Outcome { get; }

    /// <summary>
    /// The Target Metaverse Attribute's data type. Only meaningful when <see cref="Outcome"/> is
    /// <see cref="ExportMatchingOutcome.Resolved"/>.
    /// </summary>
    public AttributeDataType DataType { get; }

    /// <summary>
    /// The name of the Connected System attribute to compare against, taken from the rule's first
    /// source (<c>Sources[0].ConnectedSystemAttribute.Name</c>). Only meaningful when
    /// <see cref="Outcome"/> is <see cref="ExportMatchingOutcome.Resolved"/>.
    /// </summary>
    public string? ConnectedSystemAttributeName { get; }

    /// <summary>
    /// Whether the comparison should be case-sensitive, taken from the rule. Only meaningful when
    /// <see cref="Outcome"/> is <see cref="ExportMatchingOutcome.Resolved"/>.
    /// </summary>
    public bool CaseSensitive { get; }

    /// <summary>
    /// The typed, boxed value extracted from the Metaverse Object: a non-empty <see cref="string"/> for
    /// <see cref="AttributeDataType.Text"/>, an <see cref="int"/> for <see cref="AttributeDataType.Number"/>,
    /// a <see cref="long"/> for <see cref="AttributeDataType.LongNumber"/>, a <see cref="decimal"/> for
    /// <see cref="AttributeDataType.Decimal"/>, or a <see cref="Guid"/> for <see cref="AttributeDataType.Guid"/>.
    /// Only populated when <see cref="Outcome"/> is <see cref="ExportMatchingOutcome.Resolved"/>.
    /// </summary>
    public object? Value { get; }

    private ExportMatchingValue(ExportMatchingOutcome outcome)
    {
        Outcome = outcome;
    }

    private ExportMatchingValue(AttributeDataType dataType, string connectedSystemAttributeName, bool caseSensitive, object value)
    {
        Outcome = ExportMatchingOutcome.Resolved;
        DataType = dataType;
        ConnectedSystemAttributeName = connectedSystemAttributeName;
        CaseSensitive = caseSensitive;
        Value = value;
    }

    /// <summary>
    /// Extracts the value an Object Matching Rule would compare a Connected System Object against for
    /// the given Metaverse Object, or the reason it cannot. Mirrors
    /// <c>ConnectedSystemRepository.FindConnectedSystemObjectUsingMatchingRuleAsync</c>'s value-extraction
    /// steps exactly, up to (but not including) the database query itself.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to resolve a value from.</param>
    /// <param name="rule">The Object Matching Rule defining the source and target attributes.</param>
    public static ExportMatchingValue Resolve(MetaverseObject metaverseObject, ObjectMatchingRule rule)
    {
        if (rule.Sources.Count == 0)
            return new ExportMatchingValue(ExportMatchingOutcome.NoSources);

        if (rule.Sources.Count > 1)
            return new ExportMatchingValue(ExportMatchingOutcome.MultipleSources);

        var source = rule.Sources[0];

        // The connector-space side of the comparison always comes from the source's Connected System
        // attribute; without one there is nothing to compare CSOs on.
        if (source.ConnectedSystemAttribute == null)
            return new ExportMatchingValue(ExportMatchingOutcome.NoConnectedSystemAttribute);

        // The MVO side of the comparison: the standard rule shape (source = Connected System attribute,
        // target = Metaverse attribute, which is what the UI, API and PowerShell configure) serves both
        // import and export matching, so read the rule's Target Metaverse Attribute directly.
        var metaverseAttribute = rule.TargetMetaverseAttribute;
        if (metaverseAttribute == null)
            return new ExportMatchingValue(ExportMatchingOutcome.NoTargetMetaverseAttribute);

        var mvoAttributeValue = metaverseObject.AttributeValues
            .FirstOrDefault(av => av.AttributeId == metaverseAttribute.Id || av.Attribute?.Id == metaverseAttribute.Id);

        if (mvoAttributeValue == null)
            return new ExportMatchingValue(ExportMatchingOutcome.NoMetaverseValue);

        var connectedSystemAttributeName = source.ConnectedSystemAttribute.Name;

        return metaverseAttribute.Type switch
        {
            AttributeDataType.Text => string.IsNullOrEmpty(mvoAttributeValue.StringValue)
                ? new ExportMatchingValue(ExportMatchingOutcome.NoMetaverseValue)
                : new ExportMatchingValue(metaverseAttribute.Type, connectedSystemAttributeName, rule.CaseSensitive, mvoAttributeValue.StringValue),

            AttributeDataType.Number => mvoAttributeValue.IntValue.HasValue
                ? new ExportMatchingValue(metaverseAttribute.Type, connectedSystemAttributeName, rule.CaseSensitive, mvoAttributeValue.IntValue.Value)
                : new ExportMatchingValue(ExportMatchingOutcome.NoMetaverseValue),

            AttributeDataType.LongNumber => mvoAttributeValue.LongValue.HasValue
                ? new ExportMatchingValue(metaverseAttribute.Type, connectedSystemAttributeName, rule.CaseSensitive, mvoAttributeValue.LongValue.Value)
                : new ExportMatchingValue(ExportMatchingOutcome.NoMetaverseValue),

            AttributeDataType.Decimal => mvoAttributeValue.DecimalValue.HasValue
                ? new ExportMatchingValue(metaverseAttribute.Type, connectedSystemAttributeName, rule.CaseSensitive, mvoAttributeValue.DecimalValue.Value)
                : new ExportMatchingValue(ExportMatchingOutcome.NoMetaverseValue),

            AttributeDataType.Guid => mvoAttributeValue.GuidValue.HasValue
                ? new ExportMatchingValue(metaverseAttribute.Type, connectedSystemAttributeName, rule.CaseSensitive, mvoAttributeValue.GuidValue.Value)
                : new ExportMatchingValue(ExportMatchingOutcome.NoMetaverseValue),

            _ => new ExportMatchingValue(ExportMatchingOutcome.UnsupportedAttributeType)
        };
    }
}
