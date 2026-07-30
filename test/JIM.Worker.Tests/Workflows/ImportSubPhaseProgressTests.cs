// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Issue #637: a call-based connector's page can spend minutes inside a single ImportAsync call
/// (root DSE query, container enumeration, paged fetch), and a file-based connector reads the whole
/// file in one call. The import processor must hand connectors a progress callback and write whatever
/// they narrate straight to the Activity message, so operators can tell a healthy long-running import
/// from a stuck one.
/// </summary>
[TestFixture]
public class ImportSubPhaseProgressTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_CallConnectorReportsSubPhase_WritesItToTheActivityMessageAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        // Capture the Activity message as it stood immediately after the connector reported, because
        // the processor's own page message replaces it once the connector returns.
        var messagesSeenOnActivity = new List<string?>();
        var mockConnector = new MockSubPhaseReportingConnector(csoType, async progress =>
        {
            await progress("Querying root DSE...");
            messagesSeenOnActivity.Add(activity.Message);

            await progress("Fetching User objects from Employees (page 1)...");
            messagesSeenOnActivity.Add(activity.Message);
        });

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile,
            CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity),
            new CancellationTokenSource());

        // Act
        await processor.PerformImportAsync();

        // Assert
        Assert.That(mockConnector.ReceivedProgressCallback, Is.True,
            "The import processor must offer call-based connectors a sub-phase progress callback");
        Assert.That(messagesSeenOnActivity, Is.EqualTo(new[]
        {
            "Querying root DSE...",
            "Fetching User objects from Employees (page 1)..."
        }));
    }

    [Test]
    public async Task FullImport_FileConnectorReportsSubPhase_WritesItToTheActivityMessageAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR File");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR File Import");

        var runProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        var messagesSeenOnActivity = new List<string?>();
        var mockConnector = new MockSubPhaseReportingFileConnector(async progress =>
        {
            await progress("Reading CSV file...");
            messagesSeenOnActivity.Add(activity.Message);

            await progress("Parsed 10,000 rows...");
            messagesSeenOnActivity.Add(activity.Message);
        });

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile,
            CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity),
            new CancellationTokenSource());

        // Act
        await processor.PerformImportAsync();

        // Assert
        Assert.That(mockConnector.ReceivedProgressCallback, Is.True,
            "The import processor must offer file-based connectors a sub-phase progress callback");
        Assert.That(messagesSeenOnActivity, Is.EqualTo(new[]
        {
            "Reading CSV file...",
            "Parsed 10,000 rows..."
        }));
    }

    [Test]
    public async Task FullImport_WhenReportingTheSubPhaseFails_ImportStillCompletesAsync()
    {
        // Arrange - progress narration is cosmetic; a failure to write it must not fail the import.
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        SyncRepo.FailActivityMessageUpdateFor = "Querying root DSE...";
        var mockConnector = new MockSubPhaseReportingConnector(csoType, async progress =>
            await progress("Querying root DSE..."));

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile,
            CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity),
            new CancellationTokenSource());

        // Act
        await processor.PerformImportAsync();

        // Assert
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(3), "The import should complete despite the progress write failing");
    }

    #region Helpers

    private static SynchronisationWorkerTask CreateWorkerTask(
        int connectedSystemId, int runProfileId, Activity activity)
    {
        return new SynchronisationWorkerTask(connectedSystemId, runProfileId)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };
    }

    /// <summary>
    /// Call-based connector that narrates sub-phases through the callback JIM supplies, then returns
    /// a single page of import objects.
    /// </summary>
    private class MockSubPhaseReportingConnector(
        ConnectedSystemObjectType csoType,
        Func<Func<string, Task>, Task> narrate) : IConnector, IConnectorImportUsingCalls
    {
        public string Name => "MockConnector";
        public string? Description => null;
        public string? Url => null;

        public bool ReceivedProgressCallback { get; private set; }

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger) { }

        public void CloseImportConnection() { }

        public async Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData,
            ILogger logger,
            CancellationToken cancellationToken,
            Func<string, Task>? progressCallback = null)
        {
            ReceivedProgressCallback = progressCallback != null;
            if (progressCallback != null)
                await narrate(progressCallback);

            return new ConnectedSystemImportResult
            {
                ImportObjects = BuildImportObjects(csoType, 3)
            };
        }
    }

    /// <summary>
    /// File-based connector that narrates sub-phases through the callback JIM supplies.
    /// </summary>
    private class MockSubPhaseReportingFileConnector(Func<Func<string, Task>, Task> narrate)
        : IConnector, IConnectorImportUsingFiles
    {
        public string Name => "MockFileConnector";
        public string? Description => null;
        public string? Url => null;

        public bool ReceivedProgressCallback { get; private set; }

        public async Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            ILogger logger,
            CancellationToken cancellationToken,
            Func<string, Task>? progressCallback = null)
        {
            ReceivedProgressCallback = progressCallback != null;
            if (progressCallback != null)
                await narrate(progressCallback);

            return new ConnectedSystemImportResult { ImportObjects = [] };
        }
    }

    private static List<ConnectedSystemImportObject> BuildImportObjects(ConnectedSystemObjectType csoType, int count)
    {
        var externalIdAttribute = csoType.Attributes.First(a => a.IsExternalId);
        var importObjects = new List<ConnectedSystemImportObject>();

        for (var i = 0; i < count; i++)
        {
            importObjects.Add(new ConnectedSystemImportObject
            {
                ObjectType = csoType.Name,
                ChangeType = ObjectChangeType.Created,
                Attributes =
                [
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = externalIdAttribute.Name,
                        Type = externalIdAttribute.Type,
                        GuidValues = externalIdAttribute.Type == AttributeDataType.Guid
                            ? [Guid.NewGuid()]
                            : [],
                        StringValues = externalIdAttribute.Type == AttributeDataType.Text
                            ? [$"EXT-{i:D6}"]
                            : []
                    }
                ]
            });
        }

        return importObjects;
    }

    #endregion
}
