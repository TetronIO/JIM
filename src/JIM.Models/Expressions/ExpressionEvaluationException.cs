// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// The one error an Expression can produce (#2153, N5). The evaluator wraps every failure an Expression can cause
/// in it: a syntax error, an unknown function, a value of the wrong type, a null reference, an index out of range,
/// an arithmetic overflow, or a function refusing its input. An Expression is the administrator's configuration, so
/// its failure is reported as an expression error against the object, never as a JIM fault, and every consumer
/// catches this type alone.
/// </summary>
/// <remarks>
/// The message never carries the Expression itself: it reaches logs, and the Expression is administrator input
/// (CWE-117). Consumers that report the Expression sanitise and attach it themselves.
/// </remarks>
public class ExpressionEvaluationException : Exception
{
    /// <summary>
    /// Creates the error for a failure JIM detected itself, such as a Binary value used as text.
    /// </summary>
    public ExpressionEvaluationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the error for a failure raised while the Expression was parsed or run, keeping the original as the
    /// inner exception.
    /// </summary>
    public ExpressionEvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
