// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Thrown when an expression-based attribute flow mapping fails to evaluate during synchronisation,
/// whether inbound (CSO to MVO) or outbound (MVO to CSO export).
/// <para>
/// Synchronisation integrity requires that a thrown expression is surfaced loudly as an errored object
/// (recorded on an RPEI), never silently swallowed and never conflated with an expression that
/// deliberately evaluated to null. The orchestrating worker catches this exception and records an
/// <c>ExpressionEvaluationError</c> RPEI for the object being processed.
/// </para>
/// </summary>
public class SyncExpressionEvaluationException : Exception
{
    /// <summary>
    /// The expression that failed to evaluate. May be null if the source expression was not available.
    /// Treated as administrator-authored, but still untrusted: sanitise before logging (CWE-117).
    /// </summary>
    public string? Expression { get; }

    /// <summary>
    /// The name of the target attribute the failing mapping was flowing to (metaverse attribute for
    /// inbound, connected system attribute for outbound export). May be null if unavailable.
    /// </summary>
    public string? TargetAttributeName { get; }

    public SyncExpressionEvaluationException(string? expression, string? targetAttributeName, Exception innerException)
        : base(BuildMessage(targetAttributeName, innerException), innerException)
    {
        Expression = expression;
        TargetAttributeName = targetAttributeName;
    }

    private static string BuildMessage(string? targetAttributeName, Exception innerException)
    {
        // Deliberately exclude the raw expression from the base message so that logging the exception
        // object does not become a log-injection vector; the worker sanitises and includes the
        // expression explicitly when it records the RPEI and log line.
        return $"Expression evaluation failed for target attribute '{targetAttributeName ?? "(unknown)"}': {innerException.Message}";
    }
}
