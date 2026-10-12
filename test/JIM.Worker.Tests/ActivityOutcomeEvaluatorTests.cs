// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;

namespace JIM.Worker.Tests;

/// <summary>
/// How a finished Run Profile execution's Activity is completed from its execution items' errors. An Object Type
/// change refused at import (#2049) is a safe, deliberate outcome, like a withheld deletion: it must not fail a run on
/// its own, or whether a run with one fails would depend on how much else happened to change alongside it.
/// </summary>
[TestFixture]
public class ActivityOutcomeEvaluatorTests
{
    [Test]
    public void Evaluate_NoItemsAndNoWarning_IsComplete()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(0, 0, 0, 0), hasWarningMessage: false), Is.EqualTo(ActivityStatus.Complete));
    }

    [Test]
    public void Evaluate_NoErrorsButAWarningMessage_CompletesWithWarning()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(0, 5, 0, 0), hasWarningMessage: true), Is.EqualTo(ActivityStatus.CompleteWithWarning));
    }

    [Test]
    public void Evaluate_SomeItemsErrored_CompletesWithWarning()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(2, 5, 0, 0), hasWarningMessage: false), Is.EqualTo(ActivityStatus.CompleteWithWarning));
    }

    [Test]
    public void Evaluate_EveryItemErrored_Fails()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(5, 5, 0, 0), hasWarningMessage: false), Is.EqualTo(ActivityStatus.FailedWithError));
    }

    [Test]
    public void Evaluate_AnUnhandledErrorAmongOthers_CompletesWithError()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(2, 5, 1, 0), hasWarningMessage: false), Is.EqualTo(ActivityStatus.CompleteWithError));
    }

    [Test]
    public void Evaluate_EveryItemARefusedObjectTypeChange_CompletesWithWarning()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(1, 1, 0, 1), hasWarningMessage: false), Is.EqualTo(ActivityStatus.CompleteWithWarning));
    }

    [Test]
    public void Evaluate_RefusedObjectTypeChangesBesideOtherErrorsOnly_Fails()
    {
        // Every item that was not a refusal failed, so the run achieved nothing: the refusals do not mask that.
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(4, 4, 0, 2), hasWarningMessage: false), Is.EqualTo(ActivityStatus.FailedWithError));
    }

    [Test]
    public void Evaluate_RefusedObjectTypeChangesBesideSuccesses_CompletesWithWarning()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(2, 5, 0, 2), hasWarningMessage: false), Is.EqualTo(ActivityStatus.CompleteWithWarning));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Imports, whose unchanged objects get no item (#2066)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void Evaluate_ImportWhoseOnlyItemErroredAmongManyObjects_CompletesWithWarning()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(1, 1, 0, 0), hasWarningMessage: false, ConnectedSystemRunType.FullImport, objectsProcessed: 100),
            Is.EqualTo(ActivityStatus.CompleteWithWarning), "99 of the 100 objects imported; the run did not fail.");
    }

    [TestCase(ConnectedSystemRunType.FullImport)]
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    public void Evaluate_ImportWhereEveryObjectErrored_Fails(ConnectedSystemRunType runType)
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(3, 3, 0, 0), hasWarningMessage: false, runType, objectsProcessed: 3),
            Is.EqualTo(ActivityStatus.FailedWithError));
    }

    [Test]
    public void Evaluate_ImportThatRecordedNoObjectCount_KeepsTheItemRule()
    {
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(2, 2, 0, 0), hasWarningMessage: false, ConnectedSystemRunType.FullImport, objectsProcessed: 0),
            Is.EqualTo(ActivityStatus.FailedWithError));
    }

    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    [TestCase(ConnectedSystemRunType.Export)]
    public void Evaluate_OtherRunTypesWhereEveryItemErrored_StillFail(ConnectedSystemRunType runType)
    {
        // Their object counters describe their last step rather than the run, so they keep the item rule.
        Assert.That(ActivityOutcomeEvaluator.Evaluate(new(1, 1, 0, 0), hasWarningMessage: false, runType, objectsProcessed: 100),
            Is.EqualTo(ActivityStatus.FailedWithError));
    }
}
