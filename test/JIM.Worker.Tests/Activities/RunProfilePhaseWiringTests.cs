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
using JIM.Worker.Tests.Workflows;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Proves a real run records the steps an administrator sees (#454): that the import processor
/// enters JIM's phases as it moves through them, that a Connector's declared phases nest inside the
/// phase that calls it, and that steps a run never performs are recorded as skipped rather than
/// left looking like work still to come.
/// </summary>
[TestFixture]
public class RunProfilePhaseWiringTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_FileConnector_RecordsTheStepsItActuallyPerformedAsync()
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        var processor = BuildProcessor(connector, connectedSystem, runProfile, activity, reporter);

        // Act
        await processor.PerformImportAsync();
        await reporter.FinishAsync(failed: false);

        // Assert
        var phases = SyncRepo.ActivityPhases.OrderBy(p => p.Order).ToList();
        Assert.That(phases.Single(p => p.Key == RunPhaseKeys.ImportFetch).Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(phases.Single(p => p.Key == RunPhaseKeys.ImportSave).Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(phases.Any(p => p.Key == RunPhaseKeys.ImportConnect), Is.False,
            "A file-based import opens no connection, so the step is not part of this run's journey at all: showing it greyed out on every file-based run would say nothing");
    }

    [Test]
    public async Task FullImport_ConnectorDeclaredPhases_NestInsideThePhaseThatCallsTheConnectorAsync()
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();

        // Assert
        var readPhase = SyncRepo.ActivityPhases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read"));
        Assert.That(readPhase.ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch),
            "A Connector's step belongs inside the JIM step that called it, so the top-level step count stays the same whichever Connector is in use");
        Assert.That(readPhase.Name, Is.EqualTo("Reading the file"));
        Assert.That(readPhase.Status, Is.Not.EqualTo(ActivityPhaseStatus.Pending), "The Connector entered this step, so it cannot still be pending");
    }

    [Test]
    public async Task FullImport_ConnectorEntersAPhase_RecordsWhenItStartedAndEndedAsync()
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();
        await reporter.FinishAsync(failed: false);

        // Assert: durations are the point of persisting phases at all
        var fetch = SyncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ImportFetch);
        Assert.That(fetch.Started, Is.Not.Null);
        Assert.That(fetch.Ended, Is.Not.Null);
        Assert.That(fetch.Duration, Is.Not.Null);
        Assert.That(SyncRepo.ActivityPhases.Where(p => p.Status == ActivityPhaseStatus.Skipped).All(p => p.Started == null), Is.True,
            "A step that never ran has no duration to show");
    }

    [Test]
    public async Task FullImport_ConnectorNarratesWithinAPhase_ShowsTheMessageWithoutMovingTheStepAsync()
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType, narrateRows: true);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();

        // Assert
        var readPhase = SyncRepo.ActivityPhases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read"));
        Assert.That(SyncRepo.ActivityPhases.Count(p => p.Key == ActivityPhase.QualifyConnectorKey("read")), Is.EqualTo(1),
            "Narrating within a step must not spawn another step");
        Assert.That(readPhase.Started, Is.Not.Null);
    }

    [Test]
    public async Task FullImport_ConnectorEntersAPhaseItNeverDeclared_StillShowsAsAStepAsync()
    {
        // Arrange - a Connector that narrates something unexpected must not blank the stepper
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType, undeclaredPhaseKey: "surprise");
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();

        // Assert
        var surprise = SyncRepo.ActivityPhases.SingleOrDefault(p => p.Key == ActivityPhase.QualifyConnectorKey("surprise"));
        Assert.That(surprise, Is.Not.Null, "An undeclared phase is appended rather than dropped");
        Assert.That(surprise!.ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch));
    }

    [Test]
    public async Task FullImport_ConnectorThrowsWhileDeclaringItsPhases_RunStillRecordsJimsOwnStepsAsync()
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType, throwOnGetPhases: true);

        // Act
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();

        // Assert
        Assert.That(SyncRepo.ActivityPhases.Any(p => p.Key == RunPhaseKeys.ImportFetch), Is.True,
            "A Connector that cannot describe itself still gets to run; it just narrates less");
        Assert.That(SyncRepo.ActivityPhases.Count(p => p.Status != ActivityPhaseStatus.Pending), Is.GreaterThan(0),
            "JIM's own steps are recorded whatever the Connector does");
        // What the Connector then enters is still shown, appended rather than dropped, because a
        // step nobody declared is better than a stepper that goes blank.
        Assert.That(SyncRepo.ActivityPhases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read")).ParentKey,
            Is.EqualTo(RunPhaseKeys.ImportFetch));
    }

    [Test]
    public async Task FullImport_RunFails_RecordsTheStepItFailedInAsync()
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);
        await reporter.EnterAsync(RunPhaseKeys.ImportSave);

        // Act: the run dies while saving, which is where an administrator needs to be pointed
        await reporter.FinishAsync(failed: true);

        // Assert
        Assert.That(SyncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ImportSave).Status, Is.EqualTo(ActivityPhaseStatus.Failed));
        Assert.That(SyncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ImportReconcile).Status, Is.EqualTo(ActivityPhaseStatus.Skipped));
    }

    [Test]
    public async Task FullImport_PhaseRecordingFails_ImportStillCompletesAsync()
    {
        // Arrange - narration is cosmetic; losing a step must never cost an administrator their run
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);
        SyncRepo.FailActivityPhaseSaves = true;

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();

        // Assert
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(3));
    }

    [Test]
    public async Task FullImport_CallConnector_ShowsFetchingAsTheStepRunningWhileItFetchesAsync()
    {
        // Arrange: the step an administrator is shown must be the work actually happening. Fetching
        // objects is where a call-based import spends its time, so it has to be the running step for
        // the duration, not one closed out in milliseconds before the first page is asked for.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR System");
        ActivityPhaseReporter? reporter = null;
        var stepsRunningDuringTheFetch = new List<string?>();

        var connector = new MockPagingConnector(csoType, pages: 2, onImport: () =>
            stepsRunningDuringTheFetch.Add(reporter?.Phases?
                .FirstOrDefault(p => p.ParentKey == null && p.Status == ActivityPhaseStatus.Active)?.Key));

        reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();

        // Assert
        Assert.That(stepsRunningDuringTheFetch, Is.EqualTo(new[] { RunPhaseKeys.ImportFetch, RunPhaseKeys.ImportFetch }),
            "Every page is fetched under the fetching step, so that is the step the rail shows and the step the live progress figures are scoped to.");
    }

    [Test]
    public async Task FullImport_CallConnector_OpensTheConnectionBeforeFetchingBeginsAsync()
    {
        // Arrange: the steps are recorded in the order they are declared, so their timings have to
        // agree with that order; a fetch that started before the connection it uses reads as nonsense.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR System");
        var connector = new MockPagingConnector(csoType, pages: 1);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();
        await reporter.FinishAsync(failed: false);

        // Assert
        var connect = SyncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ImportConnect);
        var fetch = SyncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ImportFetch);
        Assert.That(connect.Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(connect.Started, Is.Not.Null);
        Assert.That(fetch.Started, Is.GreaterThanOrEqualTo(connect.Started!.Value),
            "Connecting comes first, so fetching cannot have started before it.");
    }

    [Test]
    public async Task FullImport_ConnectorHasReturned_StopsShowingItsStepAsRunningAsync()
    {
        // A Connector's step is only true while its call is in flight. A directory that hands
        // everything over in one call left "Fetching objects" shown as running for the whole time
        // JIM then spent matching what it had been given.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();

        // Assert
        var readPhase = SyncRepo.ActivityPhases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read"));
        var processPhase = SyncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ImportProcess);
        Assert.That(readPhase.Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(readPhase.Ended, Is.Not.Null, "A step nothing is running has to carry how long it took.");
        Assert.That(processPhase.Started, Is.Not.Null);
        Assert.That(readPhase.Ended, Is.LessThanOrEqualTo(processPhase.Started!.Value),
            "The Connector's step has to end when its call returns, not survive until JIM enters its next step.");
    }

    [Test]
    public async Task FullImport_MatchingWhatArrived_RecordsItAsAStepInsideTheFetchAsync()
    {
        // The work the progress figures measure for most of an import: matching what a page
        // delivered against the Connected System Objects already held. It was narrated only as a
        // message, under a step named after the fetching, so three lines said the same thing.
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync("HR File");
        var connector = new MockPhaseDeclaringFileConnector(csoType);
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(connector, connectedSystem, runProfile, activity, reporter).PerformImportAsync();

        // Assert
        var process = SyncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ImportProcess);
        Assert.That(process.Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(process.ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch));
        Assert.That(RunPhaseReading.TopLevel(SyncRepo.ActivityPhases).Count, Is.EqualTo(6),
            "A file-based import opens no connection, so it is six steps; the nested one must not make it seven.");
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType CsoType, ConnectedSystemRunProfile RunProfile, Activity Activity)>
        ArrangeImportAsync(string systemName)
    {
        var connectedSystem = await CreateConnectedSystemAsync(systemName);
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
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
        ActivityPhaseReporter reporter) =>
        new(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), connector, connectedSystem, runProfile,
            new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
            {
                Id = Guid.NewGuid(),
                Status = WorkerTaskStatus.Processing,
                Activity = activity
            },
            new CancellationTokenSource(), dbContextFactory: null, phaseReporter: reporter);

    /// <summary>
    /// A file-based Connector that declares one phase and enters it, standing in for the real File
    /// Connector without needing a file on disk.
    /// </summary>
    private class MockPhaseDeclaringFileConnector(
        ConnectedSystemObjectType csoType,
        bool narrateRows = false,
        string? undeclaredPhaseKey = null,
        bool throwOnGetPhases = false) : IConnector, IConnectorImportUsingFiles, IConnectorPhases
    {
        public string Name => "MockPhaseDeclaringFileConnector";
        public string? Description => null;
        public string? Url => null;

        public IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
        {
            if (throwOnGetPhases)
                throw new InvalidOperationException("This Connector cannot describe itself.");

            return [new ConnectorPhase("read", "Reading the file")];
        }

        public async Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            ILogger logger,
            CancellationToken cancellationToken,
            IConnectorProgress progress)
        {
            await progress.EnterPhaseAsync(undeclaredPhaseKey ?? "read");

            if (narrateRows)
            {
                await progress.ReportAsync("Parsed 10,000 rows...");
                await progress.ReportAsync("Parsed 20,000 rows...");
            }

            return new ConnectedSystemImportResult { ImportObjects = BuildImportObjects(csoType, 3) };
        }
    }

    /// <summary>
    /// A call-based Connector that returns a fixed number of pages and declares no phases of its
    /// own, standing in for the connectors whose narration cannot mask which JIM step is running.
    /// </summary>
    private class MockPagingConnector(ConnectedSystemObjectType csoType, int pages, Action? onImport = null)
        : IConnector, IConnectorImportUsingCalls
    {
        private int _pagesReturned;

        public string Name => "MockPagingConnector";
        public string? Description => null;
        public string? Url => null;

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger) { }

        public void CloseImportConnection() { }

        public Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData,
            ILogger logger,
            CancellationToken cancellationToken,
            IConnectorProgress progress)
        {
            onImport?.Invoke();
            _pagesReturned++;

            return Task.FromResult(new ConnectedSystemImportResult
            {
                ImportObjects = BuildImportObjects(csoType, 3),
                PaginationTokens = _pagesReturned < pages
                    ? [new ConnectedSystemPaginationToken("page", $"page-{_pagesReturned}")]
                    : []
            });
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
                        GuidValues = externalIdAttribute.Type == AttributeDataType.Guid ? [Guid.NewGuid()] : [],
                        StringValues = externalIdAttribute.Type == AttributeDataType.Text ? [$"EXT-{i:D6}"] : []
                    }
                ]
            });
        }

        return importObjects;
    }

    #endregion
}
