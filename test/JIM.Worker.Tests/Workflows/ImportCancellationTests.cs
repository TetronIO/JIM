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

        public string Name => "MockConnector";
        public string? Description => null;
        public string? Url => null;

        public MockPaginatedConnector(
            ConnectedSystemObjectType csoType,
            int objectsPerPage,
            int totalPages,
            Action<int>? onPageReturned = null)
        {
            _csoType = csoType;
            _objectsPerPage = objectsPerPage;
            _totalPages = totalPages;
            _onPageReturned = onPageReturned;
        }

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger) { }
        public void CloseImportConnection() { }

        public Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            // Determine current page from pagination tokens
            var currentPage = paginationTokens.Count == 0 ? 1 :
                int.Parse(paginationTokens[0].StringValue ?? "1");

            _onPageReturned?.Invoke(currentPage);

            var externalIdAttr = _csoType.Attributes.First(a => a.IsExternalId);

            var result = new ConnectedSystemImportResult
            {
                ImportObjects = new List<ConnectedSystemImportObject>()
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
