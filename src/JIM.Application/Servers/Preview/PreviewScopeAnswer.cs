// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exceptions;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Whether an object is in a Synchronisation Rule's scope, or why that cannot be decided for it. The run reports an
/// undecidable object as an error and changes nothing about it (#2250, #2054), so every preview adapter that evaluates
/// scope reports it as an object the run would fail, with the reason, and carries on with the rest (#2264). Shared so
/// every adapter words and classifies it identically.
/// </summary>
internal readonly record struct PreviewScopeAnswer(bool InScope, string? Failure)
{
    /// <summary>The attribute column a scope delta is reported under.</summary>
    internal const string AttributeName = "Scoping Criteria";

    internal const string InScopeValue = "In scope";
    internal const string OutOfScopeValue = "Out of scope";
    internal const string UndecidedValue = "Scope could not be decided";

    /// <summary>Whether the scope could not be decided for the object.</summary>
    public bool Undecided => Failure != null;

    /// <summary>The answer as a delta's value: in scope, out of scope, or why it could not be decided.</summary>
    public string Describe() => Failure != null
        ? $"{UndecidedValue}: {Failure}"
        : InScope ? InScopeValue : OutOfScopeValue;

    /// <summary>
    /// Asks <paramref name="isInScope"/>, turning a criterion that cannot be evaluated for this object into an
    /// undecided answer rather than letting it end the whole preview.
    /// </summary>
    public static PreviewScopeAnswer Decide(Func<bool> isInScope)
    {
        try
        {
            return new PreviewScopeAnswer(isInScope(), null);
        }
        catch (ScopingCriterionFailedException ex)
        {
            return new PreviewScopeAnswer(false, ex.Message);
        }
    }
}
