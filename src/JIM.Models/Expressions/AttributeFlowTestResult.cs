// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// What the Attribute Flow tester (#2055) found: either the inputs could not be held as the object's values (each
/// problem named), or the evaluation pipeline's outcome, which is what synchronisation would do with the same values.
/// </summary>
public sealed class AttributeFlowTestResult
{
    /// <summary>
    /// Why the sample inputs could not be used, one message per problem, such as text that is not a date for a date
    /// and time attribute. When any are present the Expression was not evaluated.
    /// </summary>
    public IReadOnlyList<string> InputErrors { get; init; } = [];

    /// <summary>
    /// The evaluation pipeline's outcome. Null when <see cref="InputErrors"/> stopped the test.
    /// </summary>
    public AttributeFlowEvaluation? Evaluation { get; init; }
}
