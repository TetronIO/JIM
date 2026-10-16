// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.ExampleData;

/// <summary>
/// What an Example Data Template attribute's expression gives the attribute when objects are generated (#2055).
/// </summary>
public enum ExampleDataExpressionOutcome
{
    /// <summary>
    /// A value, in JIM's text form.
    /// </summary>
    Value,

    /// <summary>
    /// No value: the expression returned none, so objects are generated without the attribute.
    /// </summary>
    NoValue,

    /// <summary>
    /// An error: generating objects from the template fails.
    /// </summary>
    Error
}

/// <summary>
/// Why an Example Data Template attribute's expression fails (#2055).
/// </summary>
public enum ExampleDataExpressionErrorReason
{
    /// <summary>
    /// The expression could not be parsed, or failed while it was evaluated.
    /// </summary>
    EvaluationFailed,

    /// <summary>
    /// The expression returned several values; a template attribute's expression must return one.
    /// </summary>
    SeveralValues,

    /// <summary>
    /// The expression returned a value with no text form, such as a Binary value.
    /// </summary>
    NoTextForm
}
