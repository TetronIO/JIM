// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The figures in a finished import's Activity message, across pages (#2129). Every object the run read is counted
/// once, as created, updated, unchanged or deleted; an object the run could not import appears only in the errors. The
/// counts are kept for the whole run, so a Connector that hands its objects over a page at a time must not change them.
/// </summary>
[TestFixture]
public class ImportOutcomeCountTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_UnchangedObjectsAcrossTwoPages_CountsEveryOneUnchangedAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var externalIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        await RunFullImportAsync(connectedSystem, TwoPagesOfTwo(type, externalIds));

        // As after an upgrade or a reset: nothing records what each object last held, so each one is compared in full.
        foreach (var cso in SyncRepo.ConnectedSystemObjects.Values)
            cso.ImportStateHash = null;

        var activity = await RunFullImportAsync(connectedSystem, TwoPagesOfTwo(type, externalIds));

        Assert.That(activity.Message, Does.Contain("4 objects (0 created, 0 updated, 4 unchanged)"),
            "Comparing an object and finding it unchanged is not an update.");
    }

    [Test]
    public async Task FullImport_DuplicatesOnOnePage_AppearOnlyInTheErrorsAsync()
    {
        // Guard, not a red test: the rejected copies were never counted as created or updated, and must not start being
        // counted as unchanged either.
        var (connectedSystem, type) = await ArrangeAsync();
        var duplicated = Guid.NewGuid();

        var activity = await RunFullImportAsync(connectedSystem,
            [[ImportObject(type, Guid.NewGuid()), ImportObject(type, duplicated), ImportObject(type, duplicated), ImportObject(type, Guid.NewGuid())]]);

        Assert.That(activity.Message, Does.Contain("4 objects (2 created, 0 updated, 2 errors)"));
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        return (connectedSystem, type);
    }

    private async Task<Activity> RunFullImportAsync(ConnectedSystem connectedSystem, List<List<ConnectedSystemImportObject>> pages)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages), connectedSystem, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        return activity;
    }

    private static List<List<ConnectedSystemImportObject>> TwoPagesOfTwo(ConnectedSystemObjectType type, List<Guid> externalIds) =>
    [
        [ImportObject(type, externalIds[0]), ImportObject(type, externalIds[1])],
        [ImportObject(type, externalIds[2]), ImportObject(type, externalIds[3])]
    ];

    private static ConnectedSystemImportObject ImportObject(ConnectedSystemObjectType type, Guid externalId) => new()
    {
        ObjectType = type.Name,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute
            {
                Name = type.Attributes.Single(a => a.IsExternalId).Name,
                Type = AttributeDataType.Guid,
                GuidValues = [externalId]
            },
            new ConnectedSystemImportObjectAttribute
            {
                Name = "DisplayName",
                Type = AttributeDataType.Text,
                StringValues = [$"User {externalId}"]
            }
        ]
    };

    #endregion
}
