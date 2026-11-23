// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Models.Localisation;

namespace JIM.Models.Expressions;

/// <summary>
/// Result of testing an expression with sample data.
/// </summary>
public class ExpressionTestResult
{
    /// <summary>
    /// Whether the expression executed successfully.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The result value from evaluating the expression.
    /// </summary>
    public object? Result { get; init; }

    /// <summary>
    /// The type name of the result value.
    /// </summary>
    public string? ResultType { get; init; }

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// <see cref="ErrorMessage"/> as a message where JIM worded it, so the portal can show it in the viewer's language;
    /// null where the text is another library's. Not part of any API response.
    /// </summary>
    [JsonIgnore]
    public JimMessage? ErrorDetail { get; init; }

    /// <summary>
    /// Creates a successful test result.
    /// </summary>
    /// <param name="result">The evaluated result.</param>
    public static ExpressionTestResult Success(object? result) => new()
    {
        IsValid = true,
        Result = result,
        ResultType = result?.GetType().Name ?? "null"
    };

    /// <summary>
    /// Creates a failed test result with an error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public static ExpressionTestResult Failure(string message)
        => new() { IsValid = false, ErrorMessage = message };

    /// <summary>
    /// Creates a failed test result from a message: <see cref="ErrorMessage"/> is its British English.
    /// </summary>
    /// <param name="detail">The error.</param>
    public static ExpressionTestResult Failure(JimMessage detail)
        => new() { IsValid = false, ErrorMessage = detail.ToEnglish(), ErrorDetail = detail };

    /// <summary>
    /// Creates a failed test result from an expression error, keeping its message where it has one.
    /// </summary>
    /// <param name="exception">The error.</param>
    public static ExpressionTestResult Failure(ExpressionEvaluationException exception)
        => new() { IsValid = false, ErrorMessage = exception.Message, ErrorDetail = exception.Detail };
}
