// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An exclusion is honoured by reading the entries it carves out and throwing them away, because a directory
/// cannot express "this subtree except that branch" in one search and decomposing the searches would make import
/// scope depend on how recently the hierarchy was refreshed (#1255). The design accepted that transfer cost on one
/// condition: that it is reported rather than hidden. These tests pin the reporting.
/// </summary>
/// <remarks>
/// The number is the evidence for revisiting the decision. An exclusion covering 500K objects inside a 510K-object
/// parent otherwise shows up as an unexplained slow import; reported, it is a figure an administrator can act on
/// by moving the branch, and the figure a future optimisation would have to justify itself against.
/// </remarks>
[TestFixture]
public class ImportExclusionDiscardReportingTests : WorkflowTestBase
{
    private const int ServiceAccountsContainerId = 51;
    private const int ArchiveContainerId = 52;

    [Test]
    public async Task FullImport_ConnectorDiscardedEntriesThroughAnExclusion_RecordsThemOnTheActivityAsync()
    {
        var (connectedSystem, runProfile, activity) = await ArrangeAsync();
        var connector = new MockDiscardReportingConnector([[new ExclusionDiscardCount(ServiceAccountsContainerId, 12)]]);

        await BuildProcessor(connector, connectedSystem, runProfile, activity).PerformImportAsync();

        Assert.That(SyncRepo.ExclusionDiscardCounts.GetValueOrDefault(activity.Id),
            Is.EquivalentTo(new Dictionary<int, long> { [ServiceAccountsContainerId] = 12 }));
    }

    [Test]
    public async Task FullImport_ExclusionDiscardsAcrossPages_AccumulatesRatherThanReportingTheLastPageAsync()
    {
        // A paged import calls the Connector once per page, and each call reports only what it read. Taking the
        // last page's figure would understate a large exclusion by however many pages preceded it, which is the
        // exact case the count exists to expose.
        var (connectedSystem, runProfile, activity) = await ArrangeAsync();
        var connector = new MockDiscardReportingConnector(
        [
            [new ExclusionDiscardCount(ServiceAccountsContainerId, 12)],
            [new ExclusionDiscardCount(ServiceAccountsContainerId, 8), new ExclusionDiscardCount(ArchiveContainerId, 3)]
        ]);

        await BuildProcessor(connector, connectedSystem, runProfile, activity).PerformImportAsync();

        Assert.That(SyncRepo.ExclusionDiscardCounts.GetValueOrDefault(activity.Id), Is.EquivalentTo(
            new Dictionary<int, long> { [ServiceAccountsContainerId] = 20, [ArchiveContainerId] = 3 }));
    }

    [Test]
    public async Task FullImport_NothingDiscarded_RecordsNothingAsync()
    {
        // Most imports discard nothing, and a zero row per Container per run would be noise in the counter table
        // and a "0 entries discarded" line on every Activity that never carried an exclusion.
        var (connectedSystem, runProfile, activity) = await ArrangeAsync();
        var connector = new MockDiscardReportingConnector([[]]);

        await BuildProcessor(connector, connectedSystem, runProfile, activity).PerformImportAsync();

        Assert.That(SyncRepo.ExclusionDiscardCounts.ContainsKey(activity.Id), Is.False);
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemRunProfile RunProfile, Activity Activity)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Corporate Directory");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "Directory Import");

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        return (connectedSystem, runProfile, activity);
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
    /// A call-based Connector that imports no objects and reports one page's worth of exclusion discards per call,
    /// which is exactly the shape a Connector filtering client-side produces.
    /// </summary>
    private class MockDiscardReportingConnector(IReadOnlyList<List<ExclusionDiscardCount>> discardsPerPage)
        : IConnector, IConnectorImportUsingCalls
    {
        private int _pagesReturned;

        public string Name => "MockDiscardReportingConnector";
        public string? Description => null;
        public string? Url => null;

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
            var page = _pagesReturned;
            _pagesReturned++;

            return Task.FromResult(new ConnectedSystemImportResult
            {
                EntriesDiscardedByExclusion = discardsPerPage[page],
                PaginationTokens = _pagesReturned < discardsPerPage.Count
                    ? [new ConnectedSystemPaginationToken("page", $"page-{_pagesReturned}")]
                    : []
            });
        }
    }

    #endregion
}
