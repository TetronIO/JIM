// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// A Metaverse Object as an export rule's scoping reads it. An asserted-null marker (#91) is not a value: scoping
/// evaluates against a real value or genuine absence, so the marker reads exactly like an absent row.
/// </summary>
internal readonly struct MvoScopingValueSource(MetaverseObject metaverseObject, IReadOnlyCollection<MetaverseAttribute>? schema = null) : IScopingValueSource
{
    public bool TryGetAttribute(SyncRuleScopingCriteria criterion, out int attributeId, out AttributeDataType type, out string name)
    {
        var attribute = criterion.MetaverseAttribute;
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

    public bool TryGetNextValue(int attributeId, AttributeDataType type, ref int position, out ScopingValue value)
    {
        var values = metaverseObject.AttributeValues;
        while (position < values.Count)
        {
            var candidate = values[position++];
            if (candidate.AttributeId != attributeId || candidate.NullValue)
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
        var values = metaverseObject.AttributeValues;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].AttributeId == attributeId && !values[i].NullValue)
                count++;
        }
        return count;
    }

    /// <remarks>A Metaverse Object always holds a value as its attribute's type, so none is held under another.</remarks>
    public AttributeDataType? HeldOnlyUnderAnotherDataType(int attributeId, AttributeDataType type) => null;

    public ExpressionContext ExpressionContext(DateTime nowUtc, out IReadOnlyDictionary<string, AttributeDataType>? heldUnderAnotherDataType)
    {
        heldUnderAnotherDataType = null;
        return new ExpressionContext(metaverseAttributes: SyncEngine.BuildAttributeDictionary(metaverseObject))
        {
            NowUtc = nowUtc
        };
    }

    public ExpressionInputSource ExpressionSide => ExpressionInputSource.Metaverse;

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

    private IReadOnlyCollection<MetaverseAttribute>? Schema =>
        schema is { Count: > 0 } ? schema : (IReadOnlyCollection<MetaverseAttribute>?)metaverseObject.Type?.Attributes;
}
