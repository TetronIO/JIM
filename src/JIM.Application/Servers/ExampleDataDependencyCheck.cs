// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Application.Servers;

/// <summary>
/// An Example Data template attribute's dependency, read once and evaluated against each generated object by the same
/// <see cref="CriterionComparer"/> scoping uses (#2053), so "Level greater than 3" means what it means in a
/// Synchronisation Rule's scope.
/// </summary>
internal sealed class ExampleDataDependencyCheck
{
    private readonly int _attributeId;
    private readonly AttributeDataType _type;
    private readonly SearchComparisonType _operator;
    private readonly ScopingValue _expected;
    private readonly bool _caseSensitive;

    internal ExampleDataDependencyCheck(int attributeId, AttributeDataType type, ICriterionValues criterion)
    {
        _attributeId = attributeId;
        _type = type;
        _operator = criterion.ComparisonType;
        _caseSensitive = criterion.CaseSensitive;
        _expected = new ScopingValue(criterion.StringValue, criterion.IntValue, criterion.LongValue, criterion.DecimalValue,
            criterion.DateTimeValue, criterion.BoolValue, criterion.GuidValue);
    }

    /// <summary>Whether the dependency holds over the values generated so far for the depended-on attribute.</summary>
    internal bool IsMet(MetaverseObject metaverseObject)
    {
        var values = new GeneratedValueCursor(metaverseObject.AttributeValues, _attributeId);
        return CriterionComparer.Evaluate(ref values, _type, _operator, _expected, _caseSensitive).Met;
    }

    /// <summary>One attribute's generated values on an object being generated.</summary>
    private struct GeneratedValueCursor : IScopingValueCursor
    {
        private readonly IReadOnlyList<MetaverseObjectAttributeValue> _values;
        private readonly int _attributeId;
        private int _position;

        public GeneratedValueCursor(IReadOnlyList<MetaverseObjectAttributeValue> values, int attributeId)
        {
            _values = values;
            _attributeId = attributeId;
            _position = 0;
        }

        public bool TryGetNext(out ScopingValue value)
        {
            while (_position < _values.Count)
            {
                var candidate = _values[_position++];
                if (candidate.Attribute.Id != _attributeId || candidate.NullValue)
                    continue;

                value = new ScopingValue(candidate.StringValue, candidate.IntValue, candidate.LongValue, candidate.DecimalValue,
                    candidate.DateTimeValue, candidate.BoolValue, candidate.GuidValue);
                return true;
            }

            value = default;
            return false;
        }
    }
}
