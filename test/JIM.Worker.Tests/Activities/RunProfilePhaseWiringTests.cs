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
