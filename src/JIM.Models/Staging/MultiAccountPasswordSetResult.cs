// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What came of setting one password across several of a person's accounts (issue #1172).
/// </summary>
public class MultiAccountPasswordSetResult
{
    /// <summary>
    /// One entry per account attempted, in the order they were attempted.
    /// </summary>
    public required IReadOnlyList<AccountPasswordSetOutcome> Outcomes { get; init; }

    public int SucceededCount => Outcomes.Count(o => o.Result.Success);

    public int FailedCount => Outcomes.Count(o => !o.Result.Success);

    /// <summary>
    /// The accounts whose password was not set, so a caller can retry exactly those and nothing else.
    /// </summary>
    public IReadOnlyList<AccountPasswordSetOutcome> Failed => Outcomes.Where(o => !o.Result.Success).ToList();

    /// <summary>
    /// Whether some accounts took the password and others did not, which is the state worth naming: the person
    /// now has a different password in the systems that refused it from the ones that accepted it.
    /// </summary>
    public bool IsPartial => SucceededCount > 0 && FailedCount > 0;
}
