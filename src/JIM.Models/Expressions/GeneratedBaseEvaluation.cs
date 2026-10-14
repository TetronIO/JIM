// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// What a Generated Value's base expression gives generation (#2055, #242), from the same evaluation pipeline that
/// synchronisation runs: the base the uniqueness token is added to, a wait, or an error. It stops before generation,
/// which needs the database to know which candidates are taken.
/// </summary>
/// <remarks>
/// Pure: building this changes nothing. Synchronisation turns it into a pending generation request or an Attribute
/// Flow error; the tester shows it.
/// </remarks>
public sealed class GeneratedBaseEvaluation
{
    /// <summary>
    /// What generation would be given.
    /// </summary>
    public required GeneratedBaseOutcome Outcome { get; init; }

    /// <summary>
    /// Why there is an error. Set only for <see cref="GeneratedBaseOutcome.Error"/>.
    /// </summary>
    public AttributeFlowErrorReason? ErrorReason { get; init; }

    /// <summary>
    /// Missing Input Behaviour and the base expression's own result. Null when the mapping has no base expression.
    /// </summary>
    public ExpressionSourceEvaluation? Source { get; init; }

    /// <summary>
    /// The base value after the mapping's value processing. Set only for <see cref="GeneratedBaseOutcome.Base"/>.
    /// </summary>
    public string? BaseValue { get; init; }

    /// <summary>
    /// The base expression's result in text, before value processing. Null when there was no single value to format.
    /// </summary>
    public string? UnprocessedBaseValue { get; init; }

    /// <summary>
    /// The value that has no text form, described, for a <see cref="AttributeFlowErrorReason.ResultNotConvertible"/>
    /// error.
    /// </summary>
    public string? UnconvertibleValue { get; init; }
}
