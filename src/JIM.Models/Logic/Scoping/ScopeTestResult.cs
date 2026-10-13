// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// What the Scope tester (#2055) found: whether an object holding the sample values is in scope of a Synchronisation
/// Rule, explained criterion by criterion exactly as synchronisation evaluates it, and when that would change.
/// </summary>
public sealed class ScopeTestResult
{
    /// <summary>
    /// Why the sample inputs could not be used, one message per problem, such as text that is not a date for a date
    /// and time attribute. When any are present the scoping criteria were not evaluated.
    /// </summary>
    public IReadOnlyList<string> InputErrors { get; init; } = [];

    /// <summary>
    /// The evaluation, from the same code synchronisation runs. Null when <see cref="InputErrors"/> stopped the test.
    /// </summary>
    public ScopingExplanation? Explanation { get; init; }

    /// <summary>
    /// When the outcome next changes after the test's instant, the values staying as they are; null when it never
    /// does, which is so whenever no relative date criterion can change it.
    /// </summary>
    public ScopeChange? NextChange { get; init; }
}
