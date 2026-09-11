// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Worker;
using NUnit.Framework;

namespace JIM.Worker.Tests;

/// <summary>
/// Unit coverage for <see cref="FullImportSuccessEvaluator.IsSuccessfulFullImport"/> (#1605, extended by
/// #1618 Run Profile Safeguards Layer 2): the predicate that decides whether a completed Full Import
/// run's Activity is trustworthy enough to arm the stranded-value sweep gate. Every
/// <see cref="ActivityStatus"/> branch is covered, including both causes of CompleteWithWarning, per the
/// #1605 PRD's explicit requirement (Functional Requirement 2), plus the #1618 withheld-deletions branch.
/// </summary>
[TestFixture]
public class FullImportSuccessEvaluatorTests
{
    [Test]
    public void IsSuccessfulFullImport_Complete_ReturnsTrue()
    {
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.Complete, objectLevelErrorCount: 0, detectedDeletionsWithheld: 0), Is.True);
    }

    [Test]
    public void IsSuccessfulFullImport_CompleteWithWarning_NoObjectLevelErrors_ReturnsTrue()
    {
        // The warning came solely from a connector-level warning message; every object imported cleanly.
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.CompleteWithWarning, objectLevelErrorCount: 0, detectedDeletionsWithheld: 0), Is.True);
    }

    [Test]
    public void IsSuccessfulFullImport_CompleteWithWarning_HasObjectLevelErrors_ReturnsFalse()
    {
        // At least one object failed to import and was never staged; counting this would let the sweep
        // gate treat that un-staged object as departed.
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.CompleteWithWarning, objectLevelErrorCount: 1, detectedDeletionsWithheld: 0), Is.False);
    }

    [Test]
    public void IsSuccessfulFullImport_CompleteWithError_ReturnsFalse()
    {
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.CompleteWithError, objectLevelErrorCount: 0, detectedDeletionsWithheld: 0), Is.False);
    }

    [Test]
    public void IsSuccessfulFullImport_FailedWithError_ReturnsFalse()
    {
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.FailedWithError, objectLevelErrorCount: 0, detectedDeletionsWithheld: 0), Is.False);
    }

    [Test]
    public void IsSuccessfulFullImport_Cancelled_ReturnsFalse()
    {
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.Cancelled, objectLevelErrorCount: 0, detectedDeletionsWithheld: 0), Is.False);
    }

    [Test]
    public void IsSuccessfulFullImport_InProgress_ReturnsFalse()
    {
        // Defensive: the worker only calls this once the Activity has been completed, but the predicate
        // itself must not treat a still-running Activity as a success.
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.InProgress, objectLevelErrorCount: 0, detectedDeletionsWithheld: 0), Is.False);
    }

    [Test]
    public void IsSuccessfulFullImport_NotSet_ReturnsFalse()
    {
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.NotSet, objectLevelErrorCount: 0, detectedDeletionsWithheld: 0), Is.False);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // detectedDeletionsWithheld (#1618 Run Profile Safeguards, Layer 2)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void IsSuccessfulFullImport_Complete_DeletionsWithheld_ReturnsFalse()
    {
        // A refused deletion detection marked nothing as deleted, so the run did not genuinely check the
        // whole Connector Space for departures: it must not arm the sweep gate, even though the Activity
        // itself completed without warning.
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.Complete, objectLevelErrorCount: 0, detectedDeletionsWithheld: 1), Is.False);
    }

    [Test]
    public void IsSuccessfulFullImport_CompleteWithWarning_NoObjectLevelErrorsButDeletionsWithheld_ReturnsFalse()
    {
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.CompleteWithWarning, objectLevelErrorCount: 0, detectedDeletionsWithheld: 4120), Is.False);
    }

    [Test]
    public void IsSuccessfulFullImport_CompleteWithWarning_ZeroDeletionsWithheld_UnchangedFromBeforeLayer2()
    {
        // Zero is "deletion detection applied and found nothing to withhold", not "did not run": it must
        // not affect the #1605 outcome at all.
        Assert.That(FullImportSuccessEvaluator.IsSuccessfulFullImport(ActivityStatus.CompleteWithWarning, objectLevelErrorCount: 0, detectedDeletionsWithheld: 0), Is.True);
    }
}
