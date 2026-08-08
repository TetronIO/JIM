// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a password channel preflight found, as a set of individually actionable checks.
/// <para>
/// This is deliberately not persisted. It is a statement about a target at one moment, and a target's
/// configuration, permissions and availability all change without JIM being told. A stored preflight would keep
/// reassuring an administrator long after it stopped being true, which is worse than not having run one.
/// </para>
/// <para>
/// A passing preflight is not a promise that a password set will succeed. Nothing short of setting a password
/// establishes that, and JIM deliberately does not do so against arbitrary objects. What a preflight rules out is
/// the large majority of failures that have nothing to do with the password itself.
/// </para>
/// </summary>
public class PasswordPreflightResult
{
    /// <summary>
    /// When the preflight ran, so that a result left on screen can be read for what it is.
    /// </summary>
    public DateTime Ran { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// What JIM identified the target as, for display alongside the checks. Null where the target could not be
    /// reached or identified.
    /// </summary>
    public string? TargetDescription { get; init; }

    /// <summary>
    /// The checks that were run, in the order they should be presented.
    /// </summary>
    public IReadOnlyList<PasswordPreflightCheckResult> Checks { get; init; } = [];

    /// <summary>
    /// The headline outcome, taken from the most serious state any check reported.
    /// <para>
    /// A failure outranks an undetermined result, which outranks a warning: the ordering is by how much it should
    /// stop an administrator, and "JIM could not tell whether this works" should stop them more than "this works
    /// but is unencrypted". Both are shown either way; this only decides the summary.
    /// </para>
    /// </summary>
    public PasswordPreflightOutcome Outcome
    {
        get
        {
            // No checks means nothing was established, which is an unknown and not a clean bill of health. Worth
            // stating rather than letting "no failures" fall through to Ready by default.
            if (Checks.Count == 0)
                return PasswordPreflightOutcome.Inconclusive;

            if (Checks.Any(c => c.State == PasswordPreflightState.Failed))
                return PasswordPreflightOutcome.NotReady;

            if (Checks.Any(c => c.State == PasswordPreflightState.CouldNotDetermine))
                return PasswordPreflightOutcome.Inconclusive;

            return Checks.Any(c => c.State == PasswordPreflightState.Warning)
                ? PasswordPreflightOutcome.ReadyWithWarnings
                : PasswordPreflightOutcome.Ready;
        }
    }
}
