// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Covers how a preflight's individual findings roll up into the one word an administrator reads first.
/// <para>
/// The property under test is arithmetic over an enum, which would not normally be worth a fixture. It is here
/// because getting it wrong produces a screen that says "Ready" over a list of problems, and a summary that
/// contradicts its own detail is worse than no summary at all.
/// </para>
/// </summary>
[TestFixture]
public class PasswordPreflightResultTests
{
    private static PasswordPreflightResult ResultWith(params PasswordPreflightState[] states)
    {
        var checks = new List<PasswordPreflightCheckResult>();
        foreach (var state in states)
            checks.Add(new PasswordPreflightCheckResult { Check = PasswordPreflightCheck.Connection, State = state, Message = "test" });

        return new PasswordPreflightResult { Checks = checks };
    }

    [Test]
    public void Outcome_WithNoChecksAtAll_IsInconclusiveRatherThanReady()
    {
        // A result carrying no findings has established nothing, so it must not read as a clean bill of health.
        // "No check failed" is trivially true of a preflight that never ran, and is the shape of false reassurance
        // this whole feature is built to avoid.
        var result = new PasswordPreflightResult();

        Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.Inconclusive));
    }

    [Test]
    public void Outcome_WithEveryCheckPassing_IsReady()
    {
        var result = ResultWith(PasswordPreflightState.Passed, PasswordPreflightState.Passed);

        Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.Ready));
    }

    [Test]
    public void Outcome_WithAWarningAmongstPasses_IsReadyWithWarnings()
    {
        var result = ResultWith(PasswordPreflightState.Passed, PasswordPreflightState.Warning);

        Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.ReadyWithWarnings));
    }

    [Test]
    public void Outcome_WithAnUndeterminedCheck_IsInconclusive()
    {
        var result = ResultWith(PasswordPreflightState.Passed, PasswordPreflightState.CouldNotDetermine);

        Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.Inconclusive));
    }

    [Test]
    public void Outcome_WithBothAWarningAndAnUndeterminedCheck_ReportsTheUndeterminedOne()
    {
        // "JIM cannot tell whether this works" should stop an administrator more firmly than "this works, but
        // insecurely", so the unknown wins the headline. Both are shown in the detail either way.
        var result = ResultWith(PasswordPreflightState.Warning, PasswordPreflightState.CouldNotDetermine);

        Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.Inconclusive));
    }

    [Test]
    public void Outcome_WithAFailureAlongsideEverythingElse_IsNotReady()
    {
        var result = ResultWith(PasswordPreflightState.Passed, PasswordPreflightState.Warning,
            PasswordPreflightState.CouldNotDetermine, PasswordPreflightState.Failed);

        Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.NotReady));
    }

    [Test]
    public void Outcome_WithAFailureAsTheOnlyFinding_IsNotReady()
    {
        var result = ResultWith(PasswordPreflightState.Failed);

        Assert.That(result.Outcome, Is.EqualTo(PasswordPreflightOutcome.NotReady));
    }
}
