using JIM.Models.Expressions;

namespace JIM.Models.Interfaces;

/// <summary>
/// Abstraction for expression evaluation, allowing future replacement of the underlying engine.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>
    /// Evaluates an expression with the given context.
    /// </summary>
    /// <param name="expression">The expression string to evaluate.</param>
    /// <param name="context">The evaluation context containing available variables.</param>
    /// <returns>The result of the expression evaluation.</returns>
    object? Evaluate(string expression, ExpressionContext context);

    /// <summary>
    /// Validates an expression without executing it.
    /// </summary>
    /// <param name="expression">The expression to validate.</param>
    /// <returns>Validation result with any errors.</returns>
    ExpressionValidationResult Validate(string expression);

    /// <summary>
    /// Tests an expression with sample data, returning the result.
    /// </summary>
    /// <param name="expression">The expression to test.</param>
    /// <param name="context">The test context with sample attribute values.</param>
    /// <returns>Test result including the evaluated value or error.</returns>
    ExpressionTestResult Test(string expression, ExpressionContext context);

    /// <summary>
    /// Gets the current expression cache metrics for diagnostics.
    /// </summary>
    /// <returns>A tuple containing (CacheHits, CacheMisses).</returns>
    (long CacheHits, long CacheMisses) GetCacheMetrics();
}
