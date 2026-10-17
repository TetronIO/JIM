// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// A parsed SCIM filter (RFC 7644 section 3.4.2.2), as an immutable tree with value equality, so two filters are equal
/// when they say the same thing in the same shape. <see cref="ScimFilterParser"/> reads one and
/// <see cref="ScimFilterWriter"/> writes one; the two invert each other exactly.
/// </summary>
public abstract record ScimFilter
{
    /// <summary>
    /// An equality assertion per value, joined by <c>or</c>: matches a resource holding any of the values at the path.
    /// </summary>
    public static ScimFilter AnyOf(ScimAttributePath path, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(values);

        ScimFilter? filter = null;
        foreach (var value in values)
        {
            var comparison = new ScimComparisonFilter(path, ScimComparisonOperator.Eq, ScimFilterValue.String(value));
            filter = filter == null ? comparison : new ScimLogicalFilter(ScimLogicalOperator.Or, filter, comparison);
        }

        return filter ?? throw new ArgumentException("A filter needs at least one value to match.", nameof(values));
    }
}
