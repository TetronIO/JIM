// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// A Metaverse Object as an export rule's scoping reads it. An asserted-null marker (#91) is not a value: scoping
/// evaluates against a real value or genuine absence, so the marker reads exactly like an absent row.
/// </summary>
internal readonly struct MvoScopingValueSource(MetaverseObject metaverseObject) : IScopingValueSource
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

    public bool TryGetFirstValue(int attributeId, out ScopingValue value)
    {
        var values = metaverseObject.AttributeValues;
        for (var i = 0; i < values.Count; i++)
        {
            var candidate = values[i];
            if (candidate.AttributeId != attributeId || candidate.NullValue)
                continue;

            value = new ScopingValue(candidate.StringValue, candidate.IntValue, candidate.LongValue, candidate.DecimalValue,
                candidate.DateTimeValue, candidate.BoolValue, candidate.GuidValue);
            return true;
        }

        value = default;
        return false;
    }

    public int CountValues(int attributeId)
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
}
