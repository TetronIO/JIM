// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Models.Localisation;

namespace JIM.Models.Expressions;

/// <summary>
/// Result of expression validation.
/// </summary>
public class ExpressionValidationResult
{
    /// <summary>
    /// Whether the expression is valid.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// <see cref="ErrorMessage"/> as a message where JIM worded it, so the portal can show it in the viewer's language;
    /// null where the text is the expression parser's. Not part of any API response.
    /// </summary>
    [JsonIgnore]
    public JimMessage? ErrorDetail { get; init; }

    /// <summary>
    /// Position in the expression where the error occurred (if applicable).
    /// </summary>
    public int? ErrorPosition { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static ExpressionValidationResult Success() => new() { IsValid = true };

    /// <summary>
    /// Creates a failed validation result with an error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="position">Optional position in the expression where the error occurred.</param>
    public static ExpressionValidationResult Failure(string message, int? position = null)
        => new() { IsValid = false, ErrorMessage = message, ErrorPosition = position };

    /// <summary>
    /// Creates a failed validation result from a message: <see cref="ErrorMessage"/> is its British English.
    /// </summary>
    /// <param name="detail">The error.</param>
    public static ExpressionValidationResult Failure(JimMessage detail)
        => new() { IsValid = false, ErrorMessage = detail.ToEnglish(), ErrorDetail = detail };
}
