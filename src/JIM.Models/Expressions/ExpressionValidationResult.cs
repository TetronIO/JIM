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
}
