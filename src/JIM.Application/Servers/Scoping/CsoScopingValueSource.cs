// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// A Connected System Object as an import rule's scoping reads it.
/// </summary>
internal readonly struct CsoScopingValueSource(ConnectedSystemObject connectedSystemObject) : IScopingValueSource
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

    public bool TryGetNextValue(int attributeId, ref int position, out ScopingValue value)
    {
        var values = connectedSystemObject.AttributeValues;
        while (position < values.Count)
        {
            var candidate = values[position++];
            if (candidate.AttributeId != attributeId)
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
        var values = connectedSystemObject.AttributeValues;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].AttributeId == attributeId)
                count++;
        }
        return count;
    }
}
