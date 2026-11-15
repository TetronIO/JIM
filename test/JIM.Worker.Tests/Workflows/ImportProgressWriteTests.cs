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
using JIM.Worker.Tests.Activities;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2521: a Connector handing over a page at a time (the LDAP Connector's Full Import since Phase 2, Active Directory, SQL
/// and SCIM always) once cost the run several Activity writes per page, each a commit: about 31,000 more at 500,000 objects
/// in pages of 100. How often an import writes its progress must not depend on how many pages it reads, and what it shows
/// must still be exactly where it got to when it ends, fails or is cancelled.
/// </summary>
[TestFixture]
public class ImportProgressWriteTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_ManyPagesWithinTheInterval_WritesNothingBetweenThemAsync()
    {
        // Arrange: the clock stands still, so every page falls within one interval of the last write.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("Many Pages");
        var writesAtEachCall = new List<int>();
        var connector = new PagingConnector(csoType, pages: 60, onCall: () => writesAtEachCall.Add(SyncRepo.ActivityWriteCount));
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);
        var processor = BuildProcessor(connector, connectedSystem, runProfile, activity, reporter, new CancellationTokenSource(), new ManualTimeProvider());

        // Act
        await processor.PerformImportAsync();

        // Assert
        Assert.That(writesAtEachCall, Has.Count.EqualTo(60));
        Assert.That(writesAtEachCall[^1] - writesAtEachCall[1], Is.Zero,
            "From the second page to the last, all within the interval, nothing needed writing: progress is paced by time, not by page");
    }

    [Test]
    public async Task FullImport_PagesSpreadOverTime_WritesAtMostOncePerIntervalAsync()
    {
        // Arrange: each page takes 300 ms of the Connector's time.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("Slow Pages");
        var clock = new ManualTimeProvider();
        var writesAtEachCall = new List<int>();
        var connector = new PagingConnector(csoType, pages: 40, onCall: () =>
        {
            writesAtEachCall.Add(SyncRepo.ActivityWriteCount);
            clock.Advance(TimeSpan.FromMilliseconds(300));
        });
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);
        var processor = BuildProcessor(connector, connectedSystem, runProfile, activity, reporter, new CancellationTokenSource(), clock);

        // Act
        await processor.PerformImportAsync();

        // Assert: pages 2 to 40 span 39 x 300 ms, under 12 intervals.
        var writesBetween = writesAtEachCall[^1] - writesAtEachCall[1];
        Assert.That(writesBetween, Is.InRange(10, 13),
            $"{writesBetween} writes over 11.7 s of pages: progress stays current to within the interval, and is written no more often");
    }

    [TestCase(false, TestName = "FullImport_ConnectorFailsPartWay_WritesTheProgressItReached")]
    [TestCase(true, TestName = "FullImport_CancelledPartWay_WritesTheProgressItReached")]
    public async Task FullImport_StoppedPartWay_WritesTheProgressItReachedAsync(bool cancel)
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("Stopped");
        var cancellation = new CancellationTokenSource();
        var connector = new PagingConnector(csoType, pages: 10, stopOnPage: 4, stop: cancel ? cancellation.Cancel : null);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);
        var processor = BuildProcessor(connector, connectedSystem, runProfile, activity, reporter, cancellation, new ManualTimeProvider());

        // Act
        try
        {
            await processor.PerformImportAsync();
        }
        catch (InvalidOperationException) when (!cancel)
        {
            // The Connector's failure, as the Worker would see it.
        }

        // Assert: what was last written is the Activity and its steps exactly as the run left them.
        Assert.That(SyncRepo.ActivityProgressWrites, Is.Not.Empty);
        var last = SyncRepo.ActivityProgressWrites[^1];
        var written = new Dictionary<string, ActivityPhase>();
        foreach (var phase in SyncRepo.ActivityProgressWrites.SelectMany(w => w.Phases))
            written[phase.Key] = phase;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.Message, Is.EqualTo(activity.Message));
            Assert.That(last.ObjectsProcessed, Is.EqualTo(activity.ObjectsProcessed));
            Assert.That(last.ObjectsToProcess, Is.EqualTo(activity.ObjectsToProcess));
            foreach (var phase in reporter.Phases!.Where(p => written.ContainsKey(p.Key)))
            {
                Assert.That(written[phase.Key].Status, Is.EqualTo(phase.Status), phase.Key);
                Assert.That(written[phase.Key].Ended, Is.EqualTo(phase.Ended), phase.Key);
            }

            Assert.That(written.ContainsKey(ActivityPhase.QualifyConnectorKey("fetch")), Is.True,
                "The Connector's step changed during the read, so its state is among what was written");
        }
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType CsoType, ConnectedSystemRunProfile RunProfile, Activity Activity)>
        ArrangeImportAsync(string systemName)
    {
        var connectedSystem = await CreateConnectedSystemAsync(systemName);
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync($"{systemName} Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, $"{systemName} Import");

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        return (connectedSystem, csoType, runProfile, activity);
    }

    private SyncImportTaskProcessor BuildProcessor(
        IConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        Activity activity,
        ActivityPhaseReporter reporter,
        CancellationTokenSource cancellation,
        TimeProvider clock) =>
        new(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), connector, connectedSystem, runProfile,
            new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
            {
                Id = Guid.NewGuid(),
                Status = WorkerTaskStatus.Processing,
                Activity = activity
            },
            cancellation, dbContextFactory: null, phaseReporter: reporter, timeProvider: clock);

    /// <summary>
    /// A Connector that hands over its objects a page at a time and narrates each call as the LDAP Connector does: it enters
    /// its fetching step and reports the objects it read. Optionally stops on one page: throws, or cancels the run; and
    /// optionally calls back at the start of each call.
    /// </summary>
    private sealed class PagingConnector(ConnectedSystemObjectType csoType, int pages, int? stopOnPage = null, Action? stop = null, Action? onCall = null)
        : IConnector, IConnectorImportUsingCalls, IConnectorPhases
    {
        private const int ObjectsPerPage = 5;
        private int _pagesReturned;

        public string Name => "PagingConnector";
        public string? Description => null;
        public string? Url => null;

        public IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile) =>
            [new ConnectorPhase("fetch", "Fetching objects")];

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger) { }

        public string? CloseImportConnection() => null;

        public async Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData,
            ILogger logger,
            CancellationToken cancellationToken,
            IConnectorProgress progress)
        {
            onCall?.Invoke();
            await progress.EnterPhaseAsync("fetch", $"Fetching page {_pagesReturned + 1}");

            if (stopOnPage == _pagesReturned + 1)
            {
                if (stop == null)
                    throw new InvalidOperationException("The directory went away.");
                stop();
            }

            var externalIdAttribute = csoType.Attributes.First(a => a.IsExternalId);
            var importObjects = Enumerable.Range(0, ObjectsPerPage).Select(i => new ConnectedSystemImportObject
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
                        StringValues = externalIdAttribute.Type == AttributeDataType.Text ? [$"EXT-{_pagesReturned:D4}-{i:D2}"] : []
                    }
                ]
            }).ToList();

            await progress.ReportObjectsReadAsync(importObjects.Count);
            _pagesReturned++;

            return new ConnectedSystemImportResult
            {
                ImportObjects = importObjects,
                PaginationTokens = _pagesReturned < pages ? [new ConnectedSystemPaginationToken("page", $"page-{_pagesReturned}")] : []
            };
        }
    }

    #endregion
}
