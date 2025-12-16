namespace JIM.Application.Expressions;

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
}
