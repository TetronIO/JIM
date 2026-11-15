// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// How much of the Connected System's Pending Export state a Full Import holds at once (#2521). A confirming import
/// reconciles each save batch's objects against their Pending Exports; it loads those Pending Exports batch by batch, never
/// every one the Connected System has, which at 525,000 objects was 9.8 million value changes held for the whole run. And
/// the deletion limits' percentage base is counted before the Connector hands over anything, so a run that saves before
/// it detects deletions measures against what JIM held when it started.
/// </summary>
[TestFixture]
public class ConfirmingImportPendingExportLoadTests : WorkflowTestBase
{
    private const int UserCount = 5;

    [Test]
    public async Task FullImport_ConfirmingAcrossSaveBatches_LoadsNoMorePendingExportsAtOnceThanOneBatchAsync()
    {
        var (connectedSystem, type, externalIds) = await ArrangeHeldUsersAsync();
        var pendingExportIds = SeedExportedRenames(type);

        // Users 0 to 3 now hold what was exported; user 4 holds something else, so its change is not confirmed.
        var (syncRepository, recorder) = RecordingSyncRepository.Wrap(SyncRepo);
        await RunFullImportAsync(connectedSystem, syncRepository, [Users(type, externalIds, index => index < 4 ? $"Renamed {index}" : "Someone else")],
            importBatchSize: 2);

        var loads = recorder.Calls.Where(c => c.Method is nameof(ISyncRepository.GetPendingExportsLightweightByConnectedSystemIdAsync)
            or nameof(ISyncRepository.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loads.Where(c => c.Method == nameof(ISyncRepository.GetPendingExportsLightweightByConnectedSystemIdAsync)), Is.Empty,
                "every Pending Export of the Connected System was loaded at once");
            Assert.That(loads.Where(c => c.Method == nameof(ISyncRepository.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync))
                .Select(c => c.Ids[0]!.Count), Has.All.LessThanOrEqualTo(2), "a load names at most one save batch's objects");
            Assert.That(pendingExportIds.Take(4).Where(SyncRepo.PendingExports.ContainsKey), Is.Empty, "confirmed in full, so deleted");
            Assert.That(SyncRepo.PendingExports[pendingExportIds[4]].AttributeValueChanges.Single().Status,
                Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed), "not confirmed, so kept to be sent again");
        }
    }

    [Test]
    public async Task FullImport_WithADeletionPercentageLimit_CountsItsBaseBeforeTheConnectorHandsOverAnythingAsync()
    {
        var (connectedSystem, type, externalIds) = await ArrangeHeldUsersAsync();

        var (syncRepository, recorder) = RecordingSyncRepository.Wrap(SyncRepo);
        var countedBeforeTheFirstPage = false;
        var activity = await RunFullImportAsync(connectedSystem, syncRepository,
            [Users(type, externalIds.Take(UserCount - 1).ToList(), index => $"User {index}")],
            beforePage: page => countedBeforeTheFirstPage |= page == 0 &&
                recorder.Calls.Any(c => c.Method == nameof(ISyncRepository.GetNonObsoleteConnectedSystemObjectCountAsync)),
            maxDetectedDeletionsPercent: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(countedBeforeTheFirstPage, Is.True, "the percentage base is what JIM held when the run started");
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(1), "one of five missing is over 10%, so refused, as before");
        }
    }

    #region Helpers

    /// <summary>A Connected System holding <see cref="UserCount"/> users, put there by a first Full Import.</summary>
    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type, List<Guid> ExternalIds)> ArrangeHeldUsersAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        var externalIds = Enumerable.Range(0, UserCount).Select(_ => Guid.NewGuid()).ToList();
        await RunFullImportAsync(connectedSystem, SyncRepo, [Users(type, externalIds, index => $"User {index}")]);
        Assert.That(SyncRepo.ConnectedSystemObjects, Has.Count.EqualTo(UserCount), "precondition: the first import created every user");
        return (connectedSystem, type, externalIds);
    }

    /// <summary>
    /// For each user, a Pending Export renaming it, exported and awaiting the confirming import, in the order the users
    /// were created.
    /// </summary>
    private List<Guid> SeedExportedRenames(ConnectedSystemObjectType type)
    {
        var displayName = type.Attributes.Single(a => a.Name == "DisplayName");
        var ids = new List<Guid>();
        // Named "User {index}" by the first import.
        var heldUsers = SyncRepo.ConnectedSystemObjects.Values
            .Select(cso => (Cso: cso, Index: cso.AttributeValues.Single(v => v.AttributeId == displayName.Id).StringValue!.Split(' ')[1]))
            .OrderBy(user => user.Index, StringComparer.Ordinal);
        foreach (var (cso, index) in heldUsers)
        {
            var pendingExport = new PendingExport
            {
                Id = Guid.NewGuid(), ConnectedSystemId = cso.ConnectedSystemId, ConnectedSystemObjectId = cso.Id, ConnectedSystemObject = cso,
                ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Exported,
                AttributeValueChanges =
                [
                    new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(), AttributeId = displayName.Id, Attribute = displayName, ChangeType = PendingExportAttributeChangeType.Update,
                        Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation, ExportAttemptCount = 1, StringValue = $"Renamed {index}"
                    }
                ]
            };
            SyncRepo.SeedPendingExport(pendingExport);
            ids.Add(pendingExport.Id);
        }

        return ids;
    }

    private async Task<Activity> RunFullImportAsync(ConnectedSystem connectedSystem, ISyncRepository syncRepository,
        List<List<ConnectedSystemImportObject>> pages, int importBatchSize = 2000, Action<int>? beforePage = null, int? maxDetectedDeletionsPercent = null)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        runProfile.MaxDetectedDeletionsPercent = maxDetectedDeletionsPercent;
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, syncRepository, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages, beforePage: beforePage), connectedSystem, runProfile, workerTask, new CancellationTokenSource())
        {
            ImportBatchSize = importBatchSize
        };
        await processor.PerformImportAsync();
        return activity;
    }

    private static List<ConnectedSystemImportObject> Users(ConnectedSystemObjectType type, List<Guid> externalIds, Func<int, string> displayName) =>
        externalIds.Select((externalId, index) => new ConnectedSystemImportObject
        {
            ObjectType = type.Name,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [externalId] },
                new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName(index)] }
            ]
        }).ToList();

    #endregion
}
