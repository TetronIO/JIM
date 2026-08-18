// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Thrown when an Expression-based Attribute Flow reads an attribute the object has no value for and the mapping's
/// Missing Input Behaviour is <c>FailObject</c>, whether inbound (Connected System Object to Metaverse Object) or
/// outbound (Metaverse Object to Connected System export).
/// <para>
/// The administrator has said that an object missing this input must not synchronise at all, so the object is
/// errored rather than partially populated: the orchestrating worker catches this and records an
/// <c>ExpressionMissingInput</c> Run Profile Execution Item, discarding the object's pending changes. Every other
/// object in the run carries on, exactly as for an Expression that threw.
/// </para>
/// <para>
/// Distinct from <see cref="SyncExpressionEvaluationException"/>: nothing failed to evaluate here. The Expression
/// would have produced a value, and the administrator asked to be told rather than have it contributed.
/// </para>
/// </summary>
public class SyncExpressionMissingInputException : Exception
{
    /// <summary>
    /// The Expression that was not evaluated. Treated as administrator-authored, but still untrusted: sanitise
    /// before logging (CWE-117).
    /// </summary>
    public string? Expression { get; }

    /// <summary>
    /// The name of the target attribute the mapping flows to (the Metaverse attribute inbound, the Connected
    /// System attribute outbound).
    /// </summary>
    public string? TargetAttributeName { get; }

    /// <summary>
    /// The inputs the object had no value for, as the Expression addresses them (for example
    /// <c>cs["lastName"]</c>).
    /// </summary>
    public IReadOnlyList<string> MissingInputs { get; }

    public SyncExpressionMissingInputException(string? expression, string? targetAttributeName, IReadOnlyList<string> missingInputs)
        : base(BuildMessage(targetAttributeName, missingInputs))
    {
        Expression = expression;
        TargetAttributeName = targetAttributeName;
        MissingInputs = missingInputs;
    }

    private static string BuildMessage(string? targetAttributeName, IReadOnlyList<string> missingInputs)
    {
        // The inputs are attribute names an administrator configured, so they belong in the message; the raw
        // Expression is deliberately left out, as it is for an Expression that threw, so that logging the
        // exception object cannot become a log-injection vector. The worker sanitises and adds it explicitly.
        var inputs = missingInputs.Count > 0 ? string.Join(", ", missingInputs) : "(unknown)";
        return $"Expression for target attribute '{targetAttributeName ?? "(unknown)"}' was not evaluated: " +
               $"no value for {inputs}. Missing Input Behaviour is set to Fail the object.";
    }
}
