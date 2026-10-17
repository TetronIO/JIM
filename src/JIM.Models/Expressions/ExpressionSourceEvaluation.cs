// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// The first half of an Attribute Flow's evaluation pipeline (#2055): Missing Input Behaviour, then the Expression
/// itself, before its result is resolved against the target attribute. A Generated Value's base expression stops
/// here; every other Attribute Flow goes on to <see cref="AttributeFlowEvaluation"/>.
/// </summary>
public sealed class ExpressionSourceEvaluation
{
    private ExpressionSourceEvaluation(ExpressionSourceOutcome outcome)
    {
        Outcome = outcome;
    }

    /// <summary>
    /// How far the Expression got.
    /// </summary>
    public ExpressionSourceOutcome Outcome { get; }

    /// <summary>
    /// The inputs the Expression reads that have no value, in accessor form (<c>cs["sn"]</c>). Populated when
    /// Missing Input Behaviour stopped the Expression being evaluated.
    /// </summary>
    public IReadOnlyList<string> MissingInputs { get; private init; } = [];

    /// <summary>
    /// The Expression's result, not yet converted: one value or a collection. Set only for
    /// <see cref="ExpressionSourceOutcome.Result"/>, and never null then.
    /// </summary>
    public object? Result { get; private init; }

    /// <summary>
    /// Why the Expression failed. Set only for <see cref="ExpressionSourceOutcome.EvaluationFailed"/>.
    /// </summary>
    public ExpressionEvaluationException? EvaluationException { get; private init; }

    /// <summary>
    /// The Expression returned this result.
    /// </summary>
    public static ExpressionSourceEvaluation Returned(object result) =>
        new(ExpressionSourceOutcome.Result) { Result = result ?? throw new ArgumentNullException(nameof(result)) };

    /// <summary>
    /// The Expression returned null.
    /// </summary>
    public static ExpressionSourceEvaluation ReturnedNull() => new(ExpressionSourceOutcome.NoValue);

    /// <summary>
    /// An input is absent and Missing Input Behaviour decided the outcome without evaluating.
    /// </summary>
    public static ExpressionSourceEvaluation MissingInput(ExpressionSourceOutcome outcome, IReadOnlyList<string> missingInputs) =>
        new(outcome) { MissingInputs = missingInputs };

    /// <summary>
    /// The inputs the Expression reads that the object holds only under another data type (#2319). Populated only for
    /// <see cref="ExpressionSourceOutcome.InputHeldUnderAnotherDataType"/>.
    /// </summary>
    public IReadOnlyList<HeldExpressionInput> HeldInputs { get; private init; } = [];

    /// <summary>
    /// An input the Expression reads is held only under another data type, so it was not evaluated.
    /// </summary>
    public static ExpressionSourceEvaluation InputHeldUnderAnotherDataType(IReadOnlyList<HeldExpressionInput> heldInputs) =>
        new(ExpressionSourceOutcome.InputHeldUnderAnotherDataType) { HeldInputs = heldInputs };

    /// <summary>
    /// The Expression failed.
    /// </summary>
    public static ExpressionSourceEvaluation Failed(ExpressionEvaluationException exception) =>
        new(ExpressionSourceOutcome.EvaluationFailed) { EvaluationException = exception };
}
