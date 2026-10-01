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
/// Tests for import processor cancellation behaviour.
/// Verifies that when cancellation is requested, the import processor stops
/// importing pages and skips persistence — discarding in-memory data cleanly.
/// </summary>
[TestFixture]
public class ImportCancellationTests : WorkflowTestBase
{
    /// <summary>
    /// Cancellation fires between pages — the processor should stop importing
    /// and skip the entire persistence phase.
    /// </summary>
    [Test]
    public async Task FullImport_CancelledBetweenPages_StopsImportingAndSkipsPersistenceAsync()
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

        var cts = new CancellationTokenSource();

        // Mock connector returns 2 pages of 3 objects each.
        // Cancel after page 1 returns — processor should stop before page 2.
        var pageCount = 0;
        var mockConnector = new MockPaginatedConnector(
            csoType,
            objectsPerPage: 3,
            totalPages: 2,
            onPageReturned: page =>
            {
                pageCount = page;
                if (page == 1)
                    cts.Cancel();
            });

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile, workerTask, cts);

        // Act
        await processor.PerformImportAsync();

        // Assert: Only page 1 was imported (cancellation stopped page 2)
        Assert.That(pageCount, Is.EqualTo(1),
            "Only 1 page should have been imported before cancellation stopped the loop");

        // Assert: No CSOs persisted (persistence phase skipped on cancellation)
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(0),
            "No CSOs should be persisted — cancellation should skip the persistence phase");
    }

    /// <summary>
    /// Pre-cancelled CTS — processor should exit before even calling the connector.
    /// </summary>
    [Test]
    public async Task FullImport_CancelledBeforeProcessing_ExitsImmediatelyAsync()
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

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        var importCalled = false;
        var mockConnector = new MockPaginatedConnector(
            csoType,
            objectsPerPage: 5,
            totalPages: 1,
            onPageReturned: _ => importCalled = true);

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile, workerTask, cts);

        // Act
        await processor.PerformImportAsync();

        // Assert: Connector was never called
        Assert.That(importCalled, Is.False,
            "Connector ImportAsync should not be called when CTS is pre-cancelled");

        // Assert: No CSOs persisted
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Regression: normal import without cancellation should persist all objects.
    /// </summary>
    [Test]
    public async Task FullImport_CompletesNormally_PersistsAllObjectsAsync()
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

        var cts = new CancellationTokenSource();

        var mockConnector = new MockPaginatedConnector(
            csoType,
            objectsPerPage: 5,
            totalPages: 1);

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile, workerTask, cts);

        // Act
        await processor.PerformImportAsync();

        // Assert: All 5 CSOs persisted
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(5),
            "All 5 CSOs should be persisted when import completes normally");
    }

    /// <summary>
    /// A connector returns its new watermark on the first page only, and the processor persists it once every page
    /// has been read. A run cancelled between pages read only part of what that watermark stands for, and the
    /// staged objects are discarded, so persisting it would make the next Delta Import start beyond changes JIM
    /// never received. The watermark a cancelled run leaves behind must be the one it started with.
    /// </summary>
    [Test]
    public async Task DeltaImport_CancelledBetweenPages_LeavesThePersistedWatermarkUnchangedAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        connectedSystem.PersistedConnectorData = "original-watermark";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Delta Import", ConnectedSystemRunType.DeltaImport);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, runProfile, ConnectedSystemRunType.DeltaImport);

        var cts = new CancellationTokenSource();

        // Two pages; the first carries the new watermark, as the LDAP connector's does. Cancel once page 1 is returned.
        var mockConnector = new MockPaginatedConnector(
            csoType,
            objectsPerPage: 3,
            totalPages: 2,
            onPageReturned: page =>
            {
                if (page == 1)
                    cts.Cancel();
            },
            firstPagePersistedConnectorData: "new-watermark");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile, workerTask, cts);

        // Act
        await processor.PerformImportAsync();

        // Assert: page 2 was never read and nothing was staged, so the watermark must not have moved on.
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(csoCount, Is.EqualTo(0), "precondition: the cancelled run staged nothing");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"),
                "a cancelled run read only part of what its watermark covers, so persisting it would skip those changes on the next Delta Import");
        }
    }

    /// <summary>
    /// The same rule for a Full Import, where the cost is larger: a cancelled Full Import stages nothing, so a
    /// baseline persisted from it would leave the next Delta Import reading only what changed after a point JIM
    /// holds no objects for.
    /// </summary>
    [Test]
    public async Task FullImport_CancelledBetweenPages_LeavesThePersistedWatermarkUnchangedAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        connectedSystem.PersistedConnectorData = "original-watermark";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        var cts = new CancellationTokenSource();
        var mockConnector = new MockPaginatedConnector(
            csoType,
            objectsPerPage: 3,
            totalPages: 2,
            onPageReturned: page =>
            {
                if (page == 1)
                    cts.Cancel();
            },
            firstPagePersistedConnectorData: "new-watermark");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile, workerTask, cts);

        // Act
        await processor.PerformImportAsync();

        // Assert
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(csoCount, Is.EqualTo(0), "precondition: the cancelled run staged nothing");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"),
                "a cancelled Full Import staged nothing, so the baseline it captured must not be recorded");
        }
    }

    /// <summary>
    /// Regression guard for the two tests above: the watermark is withheld only when the run was cancelled. A
    /// multi-page run that completes still persists the watermark the first page returned, once all pages are read.
    /// </summary>
    [Test]
    public async Task DeltaImport_CompletesNormally_PersistsTheWatermarkTheFirstPageReturnedAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        connectedSystem.PersistedConnectorData = "original-watermark";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Delta Import", ConnectedSystemRunType.DeltaImport);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, runProfile, ConnectedSystemRunType.DeltaImport);

        var mockConnector = new MockPaginatedConnector(
            csoType,
            objectsPerPage: 3,
            totalPages: 2,
            firstPagePersistedConnectorData: "new-watermark");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            mockConnector, connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        // Act
        await processor.PerformImportAsync();

        // Assert
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(csoCount, Is.EqualTo(6), "both pages were staged");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("new-watermark"));
        }
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
    /// Mock connector that implements IConnectorImportUsingCalls with controllable pagination.
    /// Returns a configurable number of pages, each with a configurable number of import objects.
    /// Optionally invokes a callback after each page returns, allowing tests to trigger cancellation.
    /// </summary>
    private class MockPaginatedConnector : IConnector, IConnectorImportUsingCalls
    {
        private readonly ConnectedSystemObjectType _csoType;
        private readonly int _objectsPerPage;
        private readonly int _totalPages;
        private readonly Action<int>? _onPageReturned;
        private readonly string? _firstPagePersistedConnectorData;

        public string Name => "MockConnector";
        public string? Description => null;
        public string? Url => null;

        public MockPaginatedConnector(
            ConnectedSystemObjectType csoType,
            int objectsPerPage,
            int totalPages,
            Action<int>? onPageReturned = null,
            string? firstPagePersistedConnectorData = null)
        {
            _csoType = csoType;
            _objectsPerPage = objectsPerPage;
            _totalPages = totalPages;
            _onPageReturned = onPageReturned;
            _firstPagePersistedConnectorData = firstPagePersistedConnectorData;
        }

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger) { }
        public string? CloseImportConnection() => null;

        public Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData,
            ILogger logger,
            CancellationToken cancellationToken,
            IConnectorProgress progress)
        {
            // Determine current page from pagination tokens
            var currentPage = paginationTokens.Count == 0 ? 1 :
                int.Parse(paginationTokens[0].StringValue ?? "1");

            _onPageReturned?.Invoke(currentPage);

            var externalIdAttr = _csoType.Attributes.First(a => a.IsExternalId);

            var result = new ConnectedSystemImportResult
            {
                ImportObjects = new List<ConnectedSystemImportObject>(),
                // A connector returns its new watermark on the first page only; later pages return null.
                PersistedConnectorData = currentPage == 1 ? _firstPagePersistedConnectorData : null
            };

            // Generate import objects for this page
            for (var i = 0; i < _objectsPerPage; i++)
            {
                var objectIndex = ((currentPage - 1) * _objectsPerPage) + i;
                var importObject = new ConnectedSystemImportObject
                {
                    ObjectType = _csoType.Name,
                    ChangeType = ObjectChangeType.Created,
                    Attributes = new List<ConnectedSystemImportObjectAttribute>
                    {
                        new()
                        {
                            Name = externalIdAttr.Name,
                            Type = externalIdAttr.Type,
                            GuidValues = externalIdAttr.Type == AttributeDataType.Guid
                                ? new List<Guid> { Guid.NewGuid() }
                                : new List<Guid>(),
                            StringValues = externalIdAttr.Type == AttributeDataType.Text
                                ? new List<string> { $"EXT-{objectIndex:D6}" }
                                : new List<string>()
                        }
                    }
                };
                result.ImportObjects.Add(importObject);
            }

            // Add pagination token for next page if not last page
            if (currentPage < _totalPages)
            {
                result.PaginationTokens = new List<ConnectedSystemPaginationToken>
                {
                    new("page", (currentPage + 1).ToString())
                };
            }

            return Task.FromResult(result);
        }
    }

    #endregion
}
