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
/// Run Profile Safeguards' deletion limits on a Delta Import (#2089). A Delta Import applies the deletes its Connector
/// reports, rather than detecting absent objects, so the limits count the Connected System Objects those deletes would
/// newly mark as deleted, across every page. Over a limit, none is applied, and the watermark the run started with is
/// kept, so the next Delta Import reads the same deletes again rather than losing them.
/// </summary>
[TestFixture]
public class DeltaImportDeletionLimitTests : WorkflowTestBase
{
    [Test]
    public async Task DeltaImport_NoLimitsSet_AppliesEveryDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 3);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [held.Select(cso => Delete(type, cso)).ToList()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(DeletionItems(activity), Has.Count.EqualTo(3));
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
            Assert.That(activity.WarningMessage, Is.Null.Or.Empty);
        }
    }

    [Test]
    public async Task DeltaImport_CountOneAboveLimit_AppliesNoDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 3);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [held.Select(cso => Delete(type, cso)).ToList()], maxCount: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal), "Every delete is withheld");
            Assert.That(DeletionItems(activity), Is.Empty, "A withheld delete gets no execution item");
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(3));
            Assert.That(activity.WarningMessage, Does.Contain("Delta Import"));
        }
    }

    [Test]
    public async Task DeltaImport_CountExactlyAtLimit_AppliesEveryDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 3);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [held.Select(cso => Delete(type, cso)).ToList()], maxCount: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
        }
    }

    [Test]
    public async Task DeltaImport_CountBelowLimit_AppliesEveryDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 3);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [held.Select(cso => Delete(type, cso)).ToList()], maxCount: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
        }
    }

    [Test]
    public async Task DeltaImport_LimitOfZero_WithholdsASingleDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 1);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Delete(type, held[0])]], maxCount: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeltaImport_LimitOfZeroAndNoDeletes_WithholdsNothingAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 1);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Update(type, held[0], "Changed")]], maxCount: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
            Assert.That(activity.WarningMessage, Is.Null.Or.Empty);
        }
    }

    [Test]
    public async Task DeltaImport_ShareOneObjectAboveLimit_AppliesNoDeleteAsync()
    {
        // 10 held, 10% allowed: 2 deletes is 20%.
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 10);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [held.Take(2).Select(cso => Delete(type, cso)).ToList()], maxPercent: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held.Take(2)), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(2));
            Assert.That(activity.WarningMessage, Does.Contain("10%"));
        }
    }

    [Test]
    public async Task DeltaImport_ShareExactlyAtLimit_AppliesEveryDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 10);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Delete(type, held[0])]], maxPercent: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held.Take(1)), Is.All.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
        }
    }

    [Test]
    public async Task DeltaImport_DeletesSpreadAcrossPages_AreCountedAsOneTotalAsync()
    {
        // No page alone exceeds the limit of 2; the run's 3 deletes do.
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 3);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem,
            [[Delete(type, held[0])], [Delete(type, held[1])], [Delete(type, held[2])]], maxCount: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(DeletionItems(activity), Is.Empty);
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task DeltaImport_ReplayedAndUnknownDeletes_AreNotCountedAsync()
    {
        // One delete that marks an object, one replayed for an object already Obsolete, one for an object JIM does not
        // hold: only the first counts, so a limit of 1 lets it through.
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 1);
        var alreadyObsolete = SeedCso(connectedSystem, type, Guid.NewGuid(), "Gone", ConnectedSystemObjectStatus.Obsolete);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem,
            [[Delete(type, held[0]), Delete(type, alreadyObsolete), Delete(type, Guid.NewGuid())]], maxCount: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
        }
    }

    [Test]
    public async Task DeltaImport_DeletedThenAddedOnALaterPage_IsNotCountedAsync()
    {
        // The re-add supersedes the delete (#2074), so only the other object is still deleted at the end of the run.
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 2);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem,
            [[Delete(type, held[0]), Delete(type, held[1])], [Update(type, held[0], "Back", ObjectChangeType.Added)]], maxCount: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.EqualTo(new[] { ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectStatus.Obsolete }));
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
        }
    }

    [Test]
    public async Task DeltaImport_DeletesWithheld_StillAppliesCreatesAndUpdatesAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 3);
        var newExternalId = Guid.NewGuid();

        var (activity, _) = await RunDeltaImportAsync(connectedSystem,
            [[Delete(type, held[0]), Delete(type, held[1]), Update(type, held[2], "Renamed"), Update(type, newExternalId, "New", ObjectChangeType.Added)]],
            maxCount: 1);

        var updated = await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, held[2].Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held.Take(2)), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(DisplayNames(updated!), Is.EqualTo(new[] { "Renamed" }), "The update is applied");
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(4), "The create is applied");
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Updated), Is.EqualTo(1));
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Added), Is.EqualTo(1));
            Assert.That(DeletionItems(activity), Is.Empty);
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(2));
        }
    }

    /// <summary>
    /// Option A of #2089: a refused Delta Import keeps the watermark it started with, so the withheld deletes are read
    /// again by the next Delta Import instead of dropping out of its view.
    /// </summary>
    [Test]
    public async Task DeltaImport_DeletesWithheld_KeepsTheWatermarkTheRunStartedWithAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 2);

        await RunDeltaImportAsync(connectedSystem, [held.Select(cso => Delete(type, cso)).ToList()], maxCount: 1, firstPageWatermark: "new-watermark");

        Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"));
    }

    [Test]
    public async Task DeltaImport_DeletesWithinTheLimits_RecordsTheNewWatermarkAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 2);

        await RunDeltaImportAsync(connectedSystem, [held.Select(cso => Delete(type, cso)).ToList()], maxCount: 2, firstPageWatermark: "new-watermark");

        Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("new-watermark"));
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        return (connectedSystem, type);
    }

    private async Task<(Activity Activity, ConnectedSystemRunProfile RunProfile)> RunDeltaImportAsync(ConnectedSystem connectedSystem,
        List<List<ConnectedSystemImportObject>> pages, int? maxCount = null, int? maxPercent = null, string? firstPageWatermark = null)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Delta Import", ConnectedSystemRunType.DeltaImport);
        runProfile.MaxDetectedDeletions = maxCount;
        runProfile.MaxDetectedDeletionsPercent = maxPercent;
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.DeltaImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages, firstPageWatermark), connectedSystem, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        return (activity, runProfile);
    }

    private static List<ActivityRunProfileExecutionItem> DeletionItems(Activity activity) =>
        activity.RunProfileExecutionItems.Where(i => i.ObjectChangeType == ObjectChangeType.Deleted).ToList();

    private async Task<List<ConnectedSystemObjectStatus>> StatusesAsync(ConnectedSystem connectedSystem, IEnumerable<ConnectedSystemObject> csos)
    {
        var statuses = new List<ConnectedSystemObjectStatus>();
        foreach (var cso in csos)
            statuses.Add((await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, cso.Id))!.Status);
        return statuses;
    }

    private List<ConnectedSystemObject> SeedHeld(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, int count) =>
        Enumerable.Range(1, count).Select(i => SeedCso(connectedSystem, type, Guid.NewGuid(), $"User {i}")).ToList();

    private ConnectedSystemObject SeedCso(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, Guid externalId, string displayName,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = type.Attributes.Single(a => a.IsExternalId).Id,
            Status = status,
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

    private static Guid ExternalIdOf(ConnectedSystemObject cso) =>
        cso.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;

    private static string?[] DisplayNames(ConnectedSystemObject cso) =>
        cso.AttributeValues.Where(v => v.Attribute?.Name == "DisplayName").Select(v => v.StringValue).ToArray();

    private static ConnectedSystemImportObject Delete(ConnectedSystemObjectType type, ConnectedSystemObject cso) => Delete(type, ExternalIdOf(cso));

    private static ConnectedSystemImportObject Delete(ConnectedSystemObjectType type, Guid externalId) =>
        ImportObject(type, externalId, null, ObjectChangeType.Deleted);

    private static ConnectedSystemImportObject Update(ConnectedSystemObjectType type, ConnectedSystemObject cso, string displayName,
        ObjectChangeType changeType = ObjectChangeType.Updated) => ImportObject(type, ExternalIdOf(cso), displayName, changeType);

    private static ConnectedSystemImportObject Update(ConnectedSystemObjectType type, Guid externalId, string displayName, ObjectChangeType changeType) =>
        ImportObject(type, externalId, displayName, changeType);

    private static ConnectedSystemImportObject ImportObject(ConnectedSystemObjectType type, Guid externalId, string? displayName, ObjectChangeType changeType)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = type.Name,
            ChangeType = changeType,
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
