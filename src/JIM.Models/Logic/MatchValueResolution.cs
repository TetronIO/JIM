// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Logic;

/// <summary>
/// What an Object Matching Rule computed for one object (#2052): the values to look up, no value, or why it could not
/// be worked out. Every path that matches (import join, export matching, both previews) acts on one of these, so they
/// agree about what a rule computes.
/// </summary>
public sealed class MatchValueResolution
{
    private MatchValueResolution(
        MatchValueResolutionKind kind,
        IReadOnlyList<object> values,
        AttributeDataType valueType,
        string? expression,
        string? failureMessage)
    {
        Kind = kind;
        Values = values;
        ValueType = valueType;
        Expression = expression;
        FailureMessage = failureMessage;
    }

    /// <summary>Which of the three outcomes this is.</summary>
    public MatchValueResolutionKind Kind { get; }

    /// <summary>
    /// The distinct values to look up, typed as the rule's Metaverse attribute stores them (<see cref="string"/>,
    /// <see cref="int"/>, <see cref="long"/>, <see cref="decimal"/> or <see cref="Guid"/>). Empty unless
    /// <see cref="Kind"/> is <see cref="MatchValueResolutionKind.Values"/>.
    /// </summary>
    public IReadOnlyList<object> Values { get; }

    /// <summary>The data type of <see cref="Values"/>: the rule's Metaverse attribute's type.</summary>
    public AttributeDataType ValueType { get; }

    /// <summary>The expression the rule evaluated, when its source is an expression.</summary>
    public string? Expression { get; }

    /// <summary>Why the rule could not be worked out. Set only when <see cref="Kind"/> is <see cref="MatchValueResolutionKind.Failed"/>.</summary>
    public string? FailureMessage { get; }

    /// <summary>The rule computed these values.</summary>
    public static MatchValueResolution WithValues(IReadOnlyList<object> values, AttributeDataType valueType, string? expression) =>
        new(MatchValueResolutionKind.Values, values, valueType, expression, null);

    /// <summary>The rule computed nothing to look up.</summary>
    public static MatchValueResolution NoValue(AttributeDataType valueType, string? expression) =>
        new(MatchValueResolutionKind.NoValue, [], valueType, expression, null);

    /// <summary>The rule could not be worked out.</summary>
    public static MatchValueResolution Failed(string failureMessage, AttributeDataType valueType, string? expression) =>
        new(MatchValueResolutionKind.Failed, [], valueType, expression, failureMessage);
}
