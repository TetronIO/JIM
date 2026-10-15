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
    /// The evaluation pipeline's outcome. Null when <see cref="InputErrors"/> stopped the test, or when the Attribute
    /// Flow generates its value (see <see cref="GeneratedBase"/>).
    /// </summary>
    public AttributeFlowEvaluation? Evaluation { get; init; }

    /// <summary>
    /// For an Attribute Flow that generates its value, what its base expression gives generation: the test stops there,
    /// as synchronisation does, since generation needs the database. Null for any other Attribute Flow, or when
    /// <see cref="InputErrors"/> stopped the test.
    /// </summary>
    public GeneratedBaseEvaluation? GeneratedBase { get; init; }

    /// <summary>
    /// The value processing step: which transforms apply and whether they changed the result (or, for a generated value,
    /// the base). Null when none can apply (an export Attribute Flow, a target that is not Text, or an Expression that
    /// produced no result).
    /// </summary>
    public AttributeFlowValueProcessing? ValueProcessing { get; init; }

    /// <summary>
    /// For each input given a value, what the Attribute Flow would do with that input absent. Empty when the inputs
    /// could not be used.
    /// </summary>
    public IReadOnlyList<AttributeFlowAbsentInputOutcome> IfInputHasNoValue { get; init; } = [];

    /// <summary>
    /// For an Attribute Flow that generates its value, what its base expression would give with each input that has a
    /// value made absent in turn. Empty for any other Attribute Flow.
    /// </summary>
    public IReadOnlyList<GeneratedBaseAbsentInputOutcome> GeneratedBaseIfInputHasNoValue { get; init; } = [];
}
