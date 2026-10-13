// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Expressions;

/// <summary>
/// What an Expression-based Attribute Flow gives its target attribute, from the one evaluation pipeline (#2055) that
/// synchronisation and the tester both run: the final values, no value, or an error, and each step on the way.
/// </summary>
/// <remarks>
/// Pure: building this changes nothing. Synchronisation turns it into pending value changes, an Attribute Flow error
/// or a failed object; the tester shows it.
/// </remarks>
public sealed class AttributeFlowEvaluation
{
    /// <summary>
    /// What the attribute would be given.
    /// </summary>
    public required AttributeFlowOutcome Outcome { get; init; }

    /// <summary>
    /// Why there is an error. Set only for <see cref="AttributeFlowOutcome.Error"/>.
    /// </summary>
    public AttributeFlowErrorReason? ErrorReason { get; init; }

    /// <summary>
    /// Missing Input Behaviour and the Expression's own result, before conversion.
    /// </summary>
    public required ExpressionSourceEvaluation Source { get; init; }

    /// <summary>
    /// The result after value processing and conversion to the target's type. Null when the Expression produced no
    /// result to resolve.
    /// </summary>
    public ExpressionResultResolution? Resolution { get; init; }

    /// <summary>
    /// The target attribute's type, which the values are converted to.
    /// </summary>
    public required AttributeDataType TargetType { get; init; }

    /// <summary>
    /// The distinct values the attribute would hold, each converted to its type. Empty unless
    /// <see cref="Outcome"/> is <see cref="AttributeFlowOutcome.Values"/>.
    /// </summary>
    public IReadOnlyList<object> Values => Outcome == AttributeFlowOutcome.Values && Resolution != null ? Resolution.Values : [];
}
