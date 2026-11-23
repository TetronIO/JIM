// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

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
public class ExpressionEvaluationException : Exception, IJimMessageCarrier
{
    /// <summary>
    /// Creates the error for a failure JIM detected itself, holding its text as a message: <see cref="Exception.Message"/>
    /// is the message's British English, which logs, stored Activities and the REST API read, and the portal renders
    /// <see cref="Detail"/> in the viewer's language (#3030).
    /// </summary>
    public ExpressionEvaluationException(JimMessage detail)
        : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    /// <summary>
    /// Creates the error for a failure raised while the Expression was run, holding its text as a message and keeping
    /// the original as the inner exception.
    /// </summary>
    public ExpressionEvaluationException(JimMessage detail, Exception innerException)
        : base(detail.ToEnglish(), innerException)
    {
        Detail = detail;
    }

    /// <summary>
    /// The error as a message, for rendering in the reader's language. Null for a failure whose text is another
    /// library's (a parser's or the runtime's), which is shown as it is.
    /// </summary>
    public JimMessage? Detail { get; }

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
