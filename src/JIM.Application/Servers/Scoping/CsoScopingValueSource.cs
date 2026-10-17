// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// A Connected System Object as an import rule's scoping reads it.
/// </summary>
internal readonly struct CsoScopingValueSource(ConnectedSystemObject connectedSystemObject, IReadOnlyCollection<ConnectedSystemObjectTypeAttribute>? schema = null) : IScopingValueSource
{
    public bool TryGetAttribute(SyncRuleScopingCriteria criterion, out int attributeId, out AttributeDataType type, out string name)
    {
        var attribute = criterion.ConnectedSystemAttribute;
        if (attribute == null)
        {
            attributeId = 0;
            type = AttributeDataType.NotSet;
            name = string.Empty;
            return false;
        }

        attributeId = attribute.Id;
        type = attribute.Type;
        name = attribute.Name;
        return true;
    }

    /// <remarks>
    /// A value that does not hold <paramref name="type"/> is skipped, as Attribute Flow skips it: one held under an
    /// earlier data type (#2128) is not this type's value. The evaluator asks <see cref="HeldOnlyUnderAnotherDataType"/>
    /// before reading any, so a value held only that way never reads as no value.
    /// </remarks>
    public bool TryGetNextValue(int attributeId, AttributeDataType type, ref int position, out ScopingValue value)
    {
        var values = connectedSystemObject.AttributeValues;
        while (position < values.Count)
        {
            var candidate = values[position++];
            if (candidate.AttributeId != attributeId || !candidate.HoldsValueOfType(type))
                continue;

            value = new ScopingValue(candidate.StringValue, candidate.IntValue, candidate.LongValue, candidate.DecimalValue,
                candidate.DateTimeValue, candidate.BoolValue, candidate.GuidValue);
            return true;
        }

        value = default;
        return false;
    }

    public int CountValues(int attributeId, AttributeDataType type)
    {
        var count = 0;
        var values = connectedSystemObject.AttributeValues;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].AttributeId == attributeId && values[i].HoldsValueOfType(type))
                count++;
        }
        return count;
    }

    /// <remarks>
    /// The rule is the one every reader applies, <see cref="SyncEngine.HeldOnlyUnderAnotherDataType"/>. It is asked only
    /// once a value of the attribute is found not to hold <paramref name="type"/>, so the usual object costs one pass
    /// and allocates nothing.
    /// </remarks>
    public AttributeDataType? HeldOnlyUnderAnotherDataType(int attributeId, AttributeDataType type)
    {
        var values = connectedSystemObject.AttributeValues;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].AttributeId == attributeId && !values[i].HoldsValueOfType(type))
                return SyncEngine.HeldOnlyUnderAnotherDataType(values, attributeId, type);
        }
        return null;
    }

    public ExpressionContext ExpressionContext(DateTime nowUtc, out IReadOnlyDictionary<string, AttributeDataType>? heldUnderAnotherDataType) =>
        new(connectedSystemAttributes: MatchValueResolver.BuildInputs(connectedSystemObject.AttributeValues, connectedSystemObject.Type,
            out heldUnderAnotherDataType))
        {
            NowUtc = nowUtc
        };

    public ExpressionInputSource ExpressionSide => ExpressionInputSource.ConnectedSystem;

    /// <remarks>
    /// The schema is the rule's object type where the caller supplies it (loaded with its attributes on every
    /// synchronisation path), else the object's own type.
    /// </remarks>
    public bool? IsInSchema(string attributeName)
    {
        var attributes = Schema;
        if (attributes is not { Count: > 0 })
            return null;

        return attributes.Any(attribute => string.Equals(attribute.Name, attributeName, StringComparison.OrdinalIgnoreCase));
    }

    public AttributeDataType? SchemaType(string attributeName) =>
        Schema?.FirstOrDefault(attribute => string.Equals(attribute.Name, attributeName, StringComparison.OrdinalIgnoreCase))?.Type;

    private IReadOnlyCollection<ConnectedSystemObjectTypeAttribute>? Schema =>
        schema is { Count: > 0 } ? schema : (IReadOnlyCollection<ConnectedSystemObjectTypeAttribute>?)connectedSystemObject.Type?.Attributes;
}
