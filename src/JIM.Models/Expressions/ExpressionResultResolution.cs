// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// An Expression's result resolved against the attribute it flows to (#2179): the distinct values that attribute
/// would hold, each already converted to its type, or why it would hold nothing. Inbound Attribute Flow, export
/// staging and the provenance inspector all resolve a result this one way, so they cannot disagree about what an
/// Expression supplies.
/// </summary>
public sealed class ExpressionResultResolution
{
    private ExpressionResultResolution(ExpressionResultResolutionKind kind)
    {
        Kind = kind;
    }

    /// <summary>
    /// What the result gives the attribute.
    /// </summary>
    public ExpressionResultResolutionKind Kind { get; }

    /// <summary>
    /// The distinct values, each converted to the attribute's type, in the order the Expression produced them.
    /// Populated only for <see cref="ExpressionResultResolutionKind.Values"/>.
    /// </summary>
    public IReadOnlyList<object> Values { get; private init; } = [];

    /// <summary>
    /// The value that could not be converted, as text for a message. Populated only for
    /// <see cref="ExpressionResultResolutionKind.NotConvertible"/>.
    /// </summary>
    public string? UnconvertibleValue { get; private init; }

    /// <summary>
    /// How many distinct values the result held. Populated only for
    /// <see cref="ExpressionResultResolutionKind.SeveralValues"/>.
    /// </summary>
    public int ValueCount { get; private init; }

    /// <summary>
    /// The attribute every value of which the result held, where it held one attribute's values (<c>cs["proxyAddresses"]</c>,
    /// or a transform of them), so the error can name it (#2053). Populated only for
    /// <see cref="ExpressionResultResolutionKind.SeveralValues"/>, and null where the values did not come from one attribute.
    /// </summary>
    public string? SourceAttributeName { get; private init; }

    /// <summary>
    /// The result supplies no value.
    /// </summary>
    public static ExpressionResultResolution NoValue() => new(ExpressionResultResolutionKind.NoValue);

    /// <summary>
    /// The result supplies these distinct, converted values.
    /// </summary>
    public static ExpressionResultResolution WithValues(IReadOnlyList<object> values) =>
        new(ExpressionResultResolutionKind.Values) { Values = values };

    /// <summary>
    /// The result held a value the attribute's type cannot hold.
    /// </summary>
    public static ExpressionResultResolution NotConvertible(string unconvertibleValue) =>
        new(ExpressionResultResolutionKind.NotConvertible) { UnconvertibleValue = unconvertibleValue };

    /// <summary>
    /// The result held more than one distinct value for a single-valued attribute.
    /// </summary>
    public static ExpressionResultResolution SeveralValues(int valueCount, string? sourceAttributeName = null) =>
        new(ExpressionResultResolutionKind.SeveralValues) { ValueCount = valueCount, SourceAttributeName = sourceAttributeName };
}
