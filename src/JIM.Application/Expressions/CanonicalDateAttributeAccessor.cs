// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// What an expression's <c>mv[...]</c> and <c>cs[...]</c> read (#2153, N4): the values the caller supplied, with every
/// date in the form JIM holds it (UTC, to the microsecond). The synchronisation builders already add dates that way
/// (<see cref="ExpressionAttributeDictionary"/>); this covers every other caller (the tester, the REST endpoint, the
/// previews), so a date stored as Kind Local never reaches an expression with the host's offset.
/// </summary>
internal sealed class CanonicalDateAttributeAccessor : AttributeAccessor
{
    // The base reads nothing of its own: every read goes to the accessor being wrapped. Never written to.
    private static readonly Dictionary<string, object?> NoAttributes = new(StringComparer.OrdinalIgnoreCase);

    private readonly AttributeAccessor _inner;

    internal CanonicalDateAttributeAccessor(AttributeAccessor inner) : base(NoAttributes)
    {
        _inner = inner;
    }

    /// <inheritdoc/>
    public override object? this[string attributeName] => Canonicalise(_inner[attributeName]);

    private static object? Canonicalise(object? value) => value switch
    {
        DateTime date => DateTimeAttributeValue.ToCanonical(date),
        DateTimeOffset dateWithOffset => DateTimeAttributeValue.ToCanonical(dateWithOffset.UtcDateTime),
        ExpressionValues values when values.Any(v => v is DateTime or DateTimeOffset) =>
            ExpressionValues.FromAttribute(values.SourceName, values.Select(Canonicalise)),
        _ => value
    };
}
