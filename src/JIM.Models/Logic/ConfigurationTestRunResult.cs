// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// How a configuration test case's run came out (#2055 Phase 4).
/// </summary>
public enum ConfigurationTestStatus
{
    /// <summary>
    /// The configuration gave what the case expects.
    /// </summary>
    Passed = 0,

    /// <summary>
    /// The configuration gave something else: the result says what was expected and what came.
    /// </summary>
    Failed = 1,

    /// <summary>
    /// The case no longer fits the configuration, so it was not run: the configuration reads an input the case gives
    /// no value for, an input left the schema or no longer fits its attribute, or what the case covers changed shape.
    /// Edit the case, not the configuration.
    /// </summary>
    NeedsReview = 2,

    /// <summary>
    /// The case could not be run at all.
    /// </summary>
    Error = 3
}

/// <summary>
/// What a configuration gave when a test case ran it.
/// </summary>
public enum ConfigurationTestOutcome
{
    /// <summary>One or more values.</summary>
    Values = 0,

    /// <summary>No value.</summary>
    NoValue = 1,

    /// <summary>An error.</summary>
    Error = 2,

    /// <summary>The object is in scope.</summary>
    InScope = 3,

    /// <summary>The object is not in scope.</summary>
    OutOfScope = 4,

    /// <summary>Synchronisation could not tell whether the object is in scope, so it would fail the object.</summary>
    Undetermined = 5
}

/// <summary>
/// What the configuration gave when a test case ran.
/// </summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Values">For <see cref="ConfigurationTestOutcome.Values"/>, the values, in their culture-invariant text
/// form; empty otherwise.</param>
/// <param name="ErrorMessage">For <see cref="ConfigurationTestOutcome.Error"/> or
/// <see cref="ConfigurationTestOutcome.Undetermined"/>, why.</param>
public sealed record ConfigurationTestActual(ConfigurationTestOutcome Outcome, IReadOnlyList<string> Values, string? ErrorMessage = null);

/// <summary>
/// One configuration test case's run. Computed each time a case runs and never stored.
/// </summary>
public sealed class ConfigurationTestRunResult
{
    public int TestCaseId { get; init; }

    public string TestCaseName { get; init; } = string.Empty;

    public ConfigurationTestCaseKind Kind { get; init; }

    public ConfigurationTestStatus Status { get; init; }

    /// <summary>
    /// One sentence saying how the run came out and why.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// For <see cref="ConfigurationTestStatus.NeedsReview"/>, each way the case no longer fits.
    /// </summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public required ConfigurationTestExpectation Expected { get; init; }

    /// <summary>
    /// What the configuration gave; null when the case was not run (<see cref="ConfigurationTestStatus.NeedsReview"/>
    /// or <see cref="ConfigurationTestStatus.Error"/>).
    /// </summary>
    public ConfigurationTestActual? Actual { get; init; }

    /// <summary>
    /// The instant the case ran as of: its own run-as-of time, or the time it ran.
    /// </summary>
    public DateTime RanAsOfUtc { get; init; }

    /// <summary>
    /// True when the case has no run-as-of time and what it covers reads the clock, so its result can differ from
    /// one run to the next. Set a run-as-of time, or expect any value, to make it repeatable.
    /// </summary>
    public bool RanAsOfNow { get; init; }
}
