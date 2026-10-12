// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.Logic;

/// <summary>
/// The values extracted from a Metaverse Object for an Object Matching Rule's export-matching lookup
/// (MVO -&gt; CSO), or the reason none could be extracted. One Metaverse Object's lookup and the page-scoped
/// candidate prefetch both resolve through <see cref="Resolve"/>, so they agree on what counts as "the
/// Metaverse Object's values" for a rule.
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
    /// <see cref="Outcome"/> is <see cref="ExportMatchingOutcome.Resolved"/>; null when the rule computes its value
    /// with an <see cref="Expression"/> instead.
    /// </summary>
    public string? ConnectedSystemAttributeName { get; }

    /// <summary>
    /// The expression the rule computes each Connected System Object's value with (#2052), when its source is one.
    /// There is no attribute to look the values up in, so the caller matches them against an index of what the
    /// expression computes for each candidate object.
    /// </summary>
    public string? Expression { get; }

    /// <summary>
    /// Whether the comparison should be case-sensitive, taken from the rule. Only meaningful when
    /// <see cref="Outcome"/> is <see cref="ExportMatchingOutcome.Resolved"/>.
    /// </summary>
    public bool CaseSensitive { get; }

    /// <summary>
    /// Every distinct, typed, boxed value the Metaverse Object holds for the Target Metaverse Attribute, in the order
    /// held (#2052): a non-empty <see cref="string"/> for <see cref="AttributeDataType.Text"/>, an <see cref="int"/>
    /// for <see cref="AttributeDataType.Number"/>, a <see cref="long"/> for <see cref="AttributeDataType.LongNumber"/>,
    /// a <see cref="decimal"/> for <see cref="AttributeDataType.Decimal"/>, or a <see cref="Guid"/> for
    /// <see cref="AttributeDataType.Guid"/>. Every value is looked up, so a multi-valued attribute cannot match on
    /// whichever value happened to load first. Only populated when <see cref="Outcome"/> is
    /// <see cref="ExportMatchingOutcome.Resolved"/>.
    /// </summary>
    public IReadOnlyList<object> Values { get; } = [];

    private ExportMatchingValue(ExportMatchingOutcome outcome)
    {
        Outcome = outcome;
    }

    private ExportMatchingValue(AttributeDataType dataType, string? connectedSystemAttributeName, string? expression, bool caseSensitive,
        IReadOnlyList<object> values)
    {
        Outcome = ExportMatchingOutcome.Resolved;
        DataType = dataType;
        ConnectedSystemAttributeName = connectedSystemAttributeName;
        Expression = expression;
        CaseSensitive = caseSensitive;
        Values = values;
    }

    /// <summary>
    /// Extracts the values an Object Matching Rule would compare Connected System Objects against for the
    /// given Metaverse Object, or the reason it cannot.
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

        // An expression is worked out per candidate object, so there is no single Connected System attribute to look
        // a value up in; the caller matches the Metaverse values through an index of what it computes instead.
        var expression = string.IsNullOrWhiteSpace(source.Expression) ? null : source.Expression;

        // Otherwise the connector-space side of the comparison comes from the source's Connected System attribute;
        // without one there is nothing to compare CSOs on.
        if (expression == null && source.ConnectedSystemAttribute == null)
            return new ExportMatchingValue(ExportMatchingOutcome.NoConnectedSystemAttribute);

        // The MVO side of the comparison: the standard rule shape (source = Connected System attribute,
        // target = Metaverse attribute, which is what the UI, API and PowerShell configure) serves both
        // import and export matching, so read the rule's Target Metaverse Attribute directly.
        var metaverseAttribute = rule.TargetMetaverseAttribute;
        if (metaverseAttribute == null)
            return new ExportMatchingValue(ExportMatchingOutcome.NoTargetMetaverseAttribute);

        if (metaverseAttribute.Type is not (AttributeDataType.Text or AttributeDataType.Number or AttributeDataType.LongNumber
            or AttributeDataType.Decimal or AttributeDataType.Guid))
        {
            return new ExportMatchingValue(ExportMatchingOutcome.UnsupportedAttributeType);
        }

        // Values of different types are never equal, so a mismatch would match nothing whatever the data held. An
        // expression's result is converted to the Metaverse attribute's type, so it cannot mismatch.
        if (expression == null && source.ConnectedSystemAttribute!.Type != metaverseAttribute.Type)
            return new ExportMatchingValue(ExportMatchingOutcome.SourceTypeMismatch);

        var values = metaverseObject.AttributeValues
            .Where(av => av.AttributeId == metaverseAttribute.Id || av.Attribute?.Id == metaverseAttribute.Id)
            .Select(av => metaverseAttribute.Type switch
            {
                AttributeDataType.Text => string.IsNullOrEmpty(av.StringValue) ? null : (object)av.StringValue,
                AttributeDataType.Number => av.IntValue,
                AttributeDataType.LongNumber => av.LongValue,
                AttributeDataType.Decimal => av.DecimalValue,
                _ => av.GuidValue
            })
            .Where(value => value != null)
            .Select(value => value!)
            .Distinct()
            .ToList();

        return values.Count == 0
            ? new ExportMatchingValue(ExportMatchingOutcome.NoMetaverseValue)
            : new ExportMatchingValue(metaverseAttribute.Type, expression == null ? source.ConnectedSystemAttribute!.Name : null, expression,
                rule.CaseSensitive, values);
    }
}
