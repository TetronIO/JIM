// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.ExampleData;

/// <summary>
/// What an Example Data Template attribute's expression gives the attribute (#2055), from the same evaluation that
/// generating objects runs: a value, no value, or an error.
/// </summary>
public sealed class ExampleDataExpressionEvaluation
{
    /// <summary>
    /// What the attribute would be given.
    /// </summary>
    public required ExampleDataExpressionOutcome Outcome { get; init; }

    /// <summary>
    /// Why there is an error. Set only for <see cref="ExampleDataExpressionOutcome.Error"/>.
    /// </summary>
    public ExampleDataExpressionErrorReason? ErrorReason { get; init; }

    /// <summary>
    /// The value the attribute would be given, in JIM's text form. Set only for
    /// <see cref="ExampleDataExpressionOutcome.Value"/>.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// The expression's own result, before it is put in text form. Null when it returned no value or failed.
    /// </summary>
    public object? Result { get; init; }

    /// <summary>
    /// Why the expression failed, as the evaluator reported it. Set only for
    /// <see cref="ExampleDataExpressionErrorReason.EvaluationFailed"/>.
    /// </summary>
    public string? EvaluationError { get; init; }
}
