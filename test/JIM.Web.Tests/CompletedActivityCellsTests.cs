// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Operations page's Completed list names a Connected System operation (a clear, a deletion) the way the Queued
/// and In Progress lists name the same work while it waits and runs: the Connected System in its own column and the
/// operation where a Run Profile would be, with what the operation removed as its outcomes.
/// </summary>
[TestFixture]
public class CompletedActivityCellsTests
{
    private static Activity ConnectedSystemOperation(ActivityTargetOperationType operation) => new()
    {
        TargetType = ActivityTargetType.ConnectedSystem,
        TargetOperationType = operation,
        TargetName = "HR System"
    };

    private static Activity RunProfileExecution() => new()
    {
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        TargetName = "Delta Sync",
        TargetContext = "HR System"
    };

    [Test]
    public void ConnectedSystem_ConnectedSystemOperation_IsTheSystemItActedOn()
    {
        Assert.That(CompletedActivityCells.ConnectedSystem(ConnectedSystemOperation(ActivityTargetOperationType.Clear)), Is.EqualTo("HR System"));
    }

    [Test]
    public void ConnectedSystem_RunProfileExecution_IsTheRunProfilesSystem()
    {
        Assert.That(CompletedActivityCells.ConnectedSystem(RunProfileExecution()), Is.EqualTo("HR System"));
    }

    [TestCase(ActivityTargetOperationType.Clear, "Clear Connected System Objects")]
    [TestCase(ActivityTargetOperationType.Delete, "Delete Connected System")]
    [TestCase(ActivityTargetOperationType.Deprovision, "Deprovision Connected System")]
    public void Operation_ConnectedSystemOperation_NamesTheOperation(ActivityTargetOperationType operation, string expected)
    {
        Assert.That(CompletedActivityCells.Operation(ConnectedSystemOperation(operation)), Is.EqualTo(expected));
    }

    [Test]
    public void Operation_RunProfileExecution_IsTheRunProfile()
    {
        Assert.That(CompletedActivityCells.Operation(RunProfileExecution()), Is.EqualTo("Delta Sync"));
    }

    [Test]
    public void ClearOutcomes_Clear_ListsWhatWasRemovedAndTheJoinsKeptForReconciliation()
    {
        var activity = ConnectedSystemOperation(ActivityTargetOperationType.Clear);
        activity.ClearedConnectedSystemObjectCount = 10_000;
        activity.ClearedPendingExportCount = 0;
        activity.ClearedJoinRecordCount = 9_412;

        var outcomes = CompletedActivityCells.ClearOutcomes(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes.Select(o => (o.Label, o.Count)), Is.EqualTo(new[]
            {
                ("Connected System Objects removed", 10_000),
                ("Joins tracked for reconciliation", 9_412)
            }), "a count of zero is left out, as every other outcome is");
            Assert.That(outcomes.Single(o => o.Count == 10_000).IsRemoval, Is.True);
            Assert.That(outcomes.Single(o => o.Count == 9_412).IsRemoval, Is.False, "tracking a join removes nothing");
        }
    }

    [Test]
    public void ClearOutcomes_RunProfileExecution_IsEmpty()
    {
        var activity = RunProfileExecution();
        activity.ClearedConnectedSystemObjectCount = 5;

        Assert.That(CompletedActivityCells.ClearOutcomes(activity), Is.Empty);
    }
}
