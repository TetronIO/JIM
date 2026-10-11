// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests;

/// <summary>
/// How a finished Run Profile's outcome is decided from its execution items. "Every item errored" is only "every object
/// failed" where every object gets an item; an import gives none to an object it found unchanged, so one bad value on an
/// otherwise unchanged import of a hundred objects read as a run that failed outright (#2066).
/// </summary>
[TestFixture]
public class RunCompletionEvaluatorTests
{
    [Test]
    public void Evaluate_ImportWhoseOnlyItemErroredAmongManyObjects_CompletesWithWarning()
    {
        var outcome = RunCompletionEvaluator.Evaluate(itemsWithErrors: 1, items: 1, unhandledErrors: 0, hasWarningMessage: false,
            ConnectedSystemRunType.FullImport, objectsProcessed: 100);

        Assert.That(outcome, Is.EqualTo(RunCompletionOutcome.CompleteWithWarning),
            "99 of the 100 objects imported; the run did not fail.");
    }

    [TestCase(ConnectedSystemRunType.FullImport)]
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    public void Evaluate_ImportWhereEveryObjectErrored_Fails(ConnectedSystemRunType runType)
    {
        var outcome = RunCompletionEvaluator.Evaluate(itemsWithErrors: 3, items: 3, unhandledErrors: 0, hasWarningMessage: false,
            runType, objectsProcessed: 3);

        Assert.That(outcome, Is.EqualTo(RunCompletionOutcome.Failed));
    }

    [Test]
    public void Evaluate_ImportThatRecordedNoObjectCount_KeepsTheItemRule()
    {
        var outcome = RunCompletionEvaluator.Evaluate(itemsWithErrors: 2, items: 2, unhandledErrors: 0, hasWarningMessage: false,
            ConnectedSystemRunType.FullImport, objectsProcessed: 0);

        Assert.That(outcome, Is.EqualTo(RunCompletionOutcome.Failed));
    }

    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    [TestCase(ConnectedSystemRunType.Export)]
    public void Evaluate_OtherRunTypesWhereEveryItemErrored_StillFail(ConnectedSystemRunType runType)
    {
        // Their object counters describe their last step rather than the run, so they keep the item rule.
        var outcome = RunCompletionEvaluator.Evaluate(itemsWithErrors: 1, items: 1, unhandledErrors: 0, hasWarningMessage: false,
            runType, objectsProcessed: 100);

        Assert.That(outcome, Is.EqualTo(RunCompletionOutcome.Failed));
    }

    [Test]
    public void Evaluate_UnhandledErrors_CompleteWithError()
    {
        var outcome = RunCompletionEvaluator.Evaluate(itemsWithErrors: 2, items: 10, unhandledErrors: 1, hasWarningMessage: false,
            ConnectedSystemRunType.FullImport, objectsProcessed: 10);

        Assert.That(outcome, Is.EqualTo(RunCompletionOutcome.CompleteWithError));
    }

    [Test]
    public void Evaluate_SomeItemsErrored_CompletesWithWarning()
    {
        var outcome = RunCompletionEvaluator.Evaluate(itemsWithErrors: 2, items: 10, unhandledErrors: 0, hasWarningMessage: false,
            ConnectedSystemRunType.FullSynchronisation, objectsProcessed: 10);

        Assert.That(outcome, Is.EqualTo(RunCompletionOutcome.CompleteWithWarning));
    }

    [Test]
    public void Evaluate_NoErrorsButAWarningMessage_CompletesWithWarning()
    {
        var outcome = RunCompletionEvaluator.Evaluate(itemsWithErrors: 0, items: 10, unhandledErrors: 0, hasWarningMessage: true,
            ConnectedSystemRunType.FullImport, objectsProcessed: 10);

        Assert.That(outcome, Is.EqualTo(RunCompletionOutcome.CompleteWithWarning));
    }

    [Test]
    public void Evaluate_NoErrorsAndNoWarning_Completes()
    {
        var outcome = RunCompletionEvaluator.Evaluate(itemsWithErrors: 0, items: 0, unhandledErrors: 0, hasWarningMessage: false,
            ConnectedSystemRunType.FullImport, objectsProcessed: 100);

        Assert.That(outcome, Is.EqualTo(RunCompletionOutcome.Complete));
    }
}
