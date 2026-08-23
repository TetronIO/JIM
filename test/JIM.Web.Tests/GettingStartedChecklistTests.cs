// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers <see cref="GettingStartedChecklist"/>, the home page's setup checklist. The step that matters most here
/// is "Run your first synchronisation": it used to be satisfied only by a Schedule having fired, so an
/// administrator who ran a Run Profile by hand (the normal way to run a first synchronisation) was never given
/// credit for it, and a system whose only Schedule was disabled could never complete the checklist at all.
/// </summary>
[TestFixture]
public class GettingStartedChecklistTests
{
    [Test]
    public void Evaluate_WhenRunProfileHasBeenExecutedButNoScheduleHasFired_MarksFirstSynchronisationComplete()
    {
        var checklist = GettingStartedChecklist.Evaluate(
            connectedSystemCount: 2,
            syncRuleCount: 4,
            scheduleCount: 1,
            anyScheduleHasRun: false,
            anyRunProfileExecuted: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(checklist.FirstSynchronisationRun, Is.True);
            Assert.That(checklist.AllComplete, Is.True);
        }
    }

    [Test]
    public void Evaluate_WhenScheduleHasFiredButNoRunProfileExecutionRecorded_MarksFirstSynchronisationComplete()
    {
        var checklist = GettingStartedChecklist.Evaluate(
            connectedSystemCount: 1,
            syncRuleCount: 1,
            scheduleCount: 1,
            anyScheduleHasRun: true,
            anyRunProfileExecuted: false);

        Assert.That(checklist.FirstSynchronisationRun, Is.True);
    }

    [Test]
    public void Evaluate_WhenNothingHasRun_LeavesFirstSynchronisationOutstanding()
    {
        var checklist = GettingStartedChecklist.Evaluate(
            connectedSystemCount: 2,
            syncRuleCount: 4,
            scheduleCount: 1,
            anyScheduleHasRun: false,
            anyRunProfileExecuted: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(checklist.FirstSynchronisationRun, Is.False);
            Assert.That(checklist.AllComplete, Is.False);
        }
    }

    [Test]
    public void Evaluate_WhenNothingIsConfigured_LeavesEveryStepOutstanding()
    {
        var checklist = GettingStartedChecklist.Evaluate(
            connectedSystemCount: 0,
            syncRuleCount: 0,
            scheduleCount: 0,
            anyScheduleHasRun: false,
            anyRunProfileExecuted: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(checklist.ConnectedSystemCreated, Is.False);
            Assert.That(checklist.SynchronisationRulesCreated, Is.False);
            Assert.That(checklist.ScheduleCreated, Is.False);
            Assert.That(checklist.FirstSynchronisationRun, Is.False);
            Assert.That(checklist.AllComplete, Is.False);
        }
    }

    [Test]
    public void Evaluate_WhenEveryStepIsSatisfied_ReportsAllComplete()
    {
        var checklist = GettingStartedChecklist.Evaluate(
            connectedSystemCount: 1,
            syncRuleCount: 1,
            scheduleCount: 1,
            anyScheduleHasRun: true,
            anyRunProfileExecuted: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(checklist.ConnectedSystemCreated, Is.True);
            Assert.That(checklist.SynchronisationRulesCreated, Is.True);
            Assert.That(checklist.ScheduleCreated, Is.True);
            Assert.That(checklist.FirstSynchronisationRun, Is.True);
            Assert.That(checklist.AllComplete, Is.True);
        }
    }
}
