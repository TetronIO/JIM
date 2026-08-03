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
/// A Connector is the only thing that can know how many objects a Connected System holds before it
/// has finished handing them over. Where it says so, the Activity shows a real percentage and a
/// time remaining for the fetching step instead of a bar with no end to it; where it says nothing,
/// the run counts up as it always has.
/// </summary>
[TestFixture]
public class ImportObjectCountReportingTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_ConnectorStatesTheObjectCount_ShowsItAsTheWorkToDoAsync()
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeAsync();
        var countsSeenDuringTheImport = new List<(int Processed, int ToProcess)>();

        var connector = new MockCountReportingConnector(csoType, objectsPerPage: 3, pages: 1, async progress =>
        {
            await progress.ReportExpectedObjectCountAsync(3);
            countsSeenDuringTheImport.Add((activity.ObjectsProcessed, activity.ObjectsToProcess));
        });

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity).PerformImportAsync();

        // Assert
        Assert.That(countsSeenDuringTheImport, Is.EqualTo(new[] { (0, 3) }),
            "The stated total has to reach the Activity while the Connector is still working, or it tells an administrator nothing they could not have waited for.");
    }

    [Test]
    public async Task FullImport_ConnectorReportsObjectsAsItProduces_CountersMoveBeforeTheCallReturnsAsync()
    {
        // Arrange - a Connector that hands everything over in one call leaves the Activity's
        // counters at zero for the whole read unless it reports its own progress.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeAsync();
        var processedSeenDuringTheImport = new List<int>();

        var connector = new MockCountReportingConnector(csoType, objectsPerPage: 3, pages: 1, async progress =>
        {
            await progress.ReportObjectsProducedAsync(1);
            processedSeenDuringTheImport.Add(activity.ObjectsProcessed);
            await progress.ReportObjectsProducedAsync(2);
            processedSeenDuringTheImport.Add(activity.ObjectsProcessed);
        });

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity).PerformImportAsync();

        // Assert
        Assert.That(processedSeenDuringTheImport, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task FullImport_ConnectorReportsWithinASecondPage_CountsFromWhatEarlierPagesDeliveredAsync()
    {
        // Arrange - a Connector counts what it is producing now; the objects earlier pages already
        // delivered are JIM's to remember, or the second page would restart the count.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeAsync();
        var processedSeenDuringTheImport = new List<int>();

        var connector = new MockCountReportingConnector(csoType, objectsPerPage: 3, pages: 2, async progress =>
        {
            await progress.ReportObjectsProducedAsync(2);
            processedSeenDuringTheImport.Add(activity.ObjectsProcessed);
        });

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity).PerformImportAsync();

        // Assert
        Assert.That(processedSeenDuringTheImport, Is.EqualTo(new[] { 2, 5 }),
            "The second page's two objects sit on top of the three the first page delivered.");
    }

    [Test]
    public async Task FullImport_ConnectorUnderstatesTheObjectCount_NeverShowsMoreDoneThanThereIsToDoAsync()
    {
        // Arrange - a stated total is the Connector's best answer, not a guarantee. More objects
        // arriving than it expected must raise the total rather than push the bar past complete.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeAsync();
        var countsSeenDuringTheImport = new List<(int Processed, int ToProcess)>();

        var connector = new MockCountReportingConnector(csoType, objectsPerPage: 3, pages: 1, async progress =>
        {
            await progress.ReportExpectedObjectCountAsync(2);
            await progress.ReportObjectsProducedAsync(3);
            countsSeenDuringTheImport.Add((activity.ObjectsProcessed, activity.ObjectsToProcess));
        });

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity).PerformImportAsync();

        // Assert
        Assert.That(countsSeenDuringTheImport, Is.EqualTo(new[] { (3, 3) }));
    }

    [Test]
    public async Task FullImport_ConnectorStatesNothing_LeavesTheFetchWithoutATotalAsync()
    {
        // Arrange - saying nothing is a valid answer, and JIM must not invent a figure in its place.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeAsync();
        var toProcessSeenDuringTheImport = new List<int>();

        var connector = new MockCountReportingConnector(csoType, objectsPerPage: 3, pages: 1, progress =>
        {
            toProcessSeenDuringTheImport.Add(activity.ObjectsToProcess);
            return Task.CompletedTask;
        });

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity).PerformImportAsync();

        // Assert
        Assert.That(toProcessSeenDuringTheImport, Is.EqualTo(new[] { 0 }),
            "No total is honest; a made-up one is not.");
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType CsoType, ConnectedSystemRunProfile RunProfile, Activity Activity)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        return (connectedSystem, csoType, runProfile, activity);
    }

    private SyncImportTaskProcessor BuildProcessor(
        IConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        Activity activity) =>
        new(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), connector, connectedSystem, runProfile,
            new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
            {
                Id = Guid.NewGuid(),
                Status = WorkerTaskStatus.Processing,
                Activity = activity
            },
            new CancellationTokenSource());

    /// <summary>
    /// A call-based Connector that reports counts through the callback JIM supplies, then returns a
    /// fixed number of pages of objects.
    /// </summary>
    private class MockCountReportingConnector(
        ConnectedSystemObjectType csoType,
        int objectsPerPage,
        int pages,
        Func<IConnectorProgress, Task> report) : IConnector, IConnectorImportUsingCalls
    {
        private int _pagesReturned;

        public string Name => "MockCountReportingConnector";
        public string? Description => null;
        public string? Url => null;

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger) { }

        public void CloseImportConnection() { }

        public async Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData,
            ILogger logger,
            CancellationToken cancellationToken,
            IConnectorProgress progress)
        {
            await report(progress);
            _pagesReturned++;

            return new ConnectedSystemImportResult
            {
                ImportObjects = BuildImportObjects(csoType, objectsPerPage, (_pagesReturned - 1) * objectsPerPage),
                PaginationTokens = _pagesReturned < pages
                    ? [new ConnectedSystemPaginationToken("page", $"page-{_pagesReturned}")]
                    : []
            };
        }
    }

    private static List<ConnectedSystemImportObject> BuildImportObjects(ConnectedSystemObjectType csoType, int count, int startAt)
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
                        GuidValues = externalIdAttribute.Type == AttributeDataType.Guid ? [Guid.NewGuid()] : [],
                        StringValues = externalIdAttribute.Type == AttributeDataType.Text ? [$"EXT-{startAt + i:D6}"] : []
                    }
                ]
            });
        }

        return importObjects;
    }

    #endregion
}
