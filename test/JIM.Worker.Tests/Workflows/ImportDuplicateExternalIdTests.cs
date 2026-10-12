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
/// Two imported objects with the same External ID (#2063). Within one page JIM has no reason to prefer either copy, so
/// both are rejected and nothing is created (#280). Across pages the earlier copy has already been processed, so the
/// later one is rejected. Connectors that import by calls page their results, and used to have every same-page repeat
/// treated as a cross-page one: the first copy was imported and only the second rejected.
/// </summary>
[TestFixture]
public class ImportDuplicateExternalIdTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_SameExternalIdTwiceOnOnePage_RejectsBothAndCreatesNothingAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var externalId = Guid.NewGuid();

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, externalId), ImportObject(type, externalId)]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DuplicateItems(activity), Has.Count.EqualTo(2), "Both copies are rejected");
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.Zero, "Neither copy is imported");
        }
    }

    [Test]
    public async Task FullImport_SameExternalIdThreeTimesOnOnePage_RejectsAllThreeAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var externalId = Guid.NewGuid();

        var activity = await RunImportAsync(connectedSystem,
            [[ImportObject(type, externalId), ImportObject(type, externalId), ImportObject(type, externalId)]]);

        Assert.That(DuplicateItems(activity), Has.Count.EqualTo(3));
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.Zero);
    }

    [Test]
    public async Task FullImport_SameExternalIdOnTwoPages_ImportsTheFirstAndRejectsTheLaterAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var externalId = Guid.NewGuid();

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, externalId)], [ImportObject(type, externalId)]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DuplicateItems(activity), Has.Count.EqualTo(1), "Only the later copy is rejected");
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(1), "The earlier copy was imported");
        }
    }

    [Test]
    public async Task FullImport_RepeatOnOnePageThenAgainOnTheNext_RejectsEveryCopyAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var externalId = Guid.NewGuid();

        var activity = await RunImportAsync(connectedSystem,
            [[ImportObject(type, externalId), ImportObject(type, externalId)], [ImportObject(type, externalId)]]);

        Assert.That(DuplicateItems(activity), Has.Count.EqualTo(3));
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.Zero);
    }

    [Test]
    public async Task FullImport_ObjectJimHoldsReturnedTwiceOnOnePage_RejectsBothAndLeavesItUnchangedAsync()
    {
        // Rejecting the first copy only once the second turns up came too late for an object JIM already holds: the
        // first copy's changes had been applied, and were saved while its item reported them rejected.
        var (connectedSystem, type) = await ArrangeAsync();
        var externalId = Guid.NewGuid();
        var held = SeedCso(connectedSystem, type, externalId, "Original");

        var activity = await RunImportAsync(connectedSystem,
            [[ImportObject(type, externalId, "First copy"), ImportObject(type, externalId, "Second copy")]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DuplicateItems(activity), Has.Count.EqualTo(2));
            Assert.That(DisplayNames(held), Is.EqualTo(new[] { "Original" }), "Neither copy's changes are applied");
            Assert.That(held.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "A rejected copy still counts as returned, so nothing is deleted");
        }
    }

    [Test]
    public async Task FullImport_DuplicatesOnOnePage_DoNotAffectUniqueObjectsAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var duplicated = Guid.NewGuid();

        var activity = await RunImportAsync(connectedSystem,
            [[ImportObject(type, Guid.NewGuid()), ImportObject(type, duplicated), ImportObject(type, duplicated), ImportObject(type, Guid.NewGuid())]]);

        Assert.That(DuplicateItems(activity), Has.Count.EqualTo(2));
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(2));
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        return (connectedSystem, type);
    }

    private async Task<Activity> RunImportAsync(ConnectedSystem connectedSystem, List<List<ConnectedSystemImportObject>> pages)
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

    private static List<ActivityRunProfileExecutionItem> DuplicateItems(Activity activity) =>
        activity.RunProfileExecutionItems
            .Where(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.DuplicateObject)
            .ToList();

    private ConnectedSystemObject SeedCso(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, Guid externalId, string displayName)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = type.Attributes.Single(a => a.IsExternalId).Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        foreach (var (attribute, guidValue, stringValue) in new[]
                 {
                     (type.Attributes.Single(a => a.IsExternalId), (Guid?)externalId, (string?)null),
                     (type.Attributes.Single(a => a.Name == "DisplayName"), null, displayName)
                 })
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = attribute.Id,
                Attribute = attribute,
                GuidValue = guidValue,
                StringValue = stringValue,
                ConnectedSystemObject = cso
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static string?[] DisplayNames(ConnectedSystemObject cso) =>
        cso.AttributeValues.Where(v => v.Attribute?.Name == "DisplayName").Select(v => v.StringValue).ToArray();

    private static ConnectedSystemImportObject ImportObject(ConnectedSystemObjectType type, Guid externalId, string? displayName = null)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = type.Name,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = type.Attributes.Single(a => a.IsExternalId).Name,
                    Type = AttributeDataType.Guid,
                    GuidValues = [externalId]
                }
            ]
        };
        if (displayName != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName] });
        return importObject;
    }

    #endregion
}
