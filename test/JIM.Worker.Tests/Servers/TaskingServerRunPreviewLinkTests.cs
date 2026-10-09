// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The link from a Full Synchronisation run back to the preview the administrator read before starting it (#1530).
///
/// The run's Activity is where the audit trail answers "did they look first, and what were they told?", so the link
/// is only recorded when it says something true: the preview must be a finished Full Synchronisation preview of the
/// same Connected System, cited by a Full Synchronisation run. Anything else is refused rather than recorded, because
/// a run that queued with a wrong link would carry it for as long as its Activity is kept. The check lives where every
/// run is queued (portal, REST and scheduler alike), so no path can record a link the others would refuse.
/// </summary>
[TestFixture]
public class TaskingServerRunPreviewLinkTests
{
    private const int ConnectedSystemId = 7;
    private const int OtherConnectedSystemId = 8;
    private const int FullSynchronisationProfileId = 100;
    private const int DeltaSynchronisationProfileId = 101;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<ITaskingRepository> _tasking = null!;
    private Mock<IActivityRepository> _activities = null!;
    private Mock<IConfigurationChangePreviewRepository> _previews = null!;
    private JimApplication _application = null!;
    private readonly List<Activity> _createdActivities = [];
    private readonly Dictionary<Guid, Activity> _storedActivities = [];
    private readonly Dictionary<Guid, ConfigurationChangePreview> _storedPreviews = [];
    private ConfigurationChangePreviewStaleness _staleness = new(null, null);

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _createdActivities.Clear();
        _storedActivities.Clear();
        _storedPreviews.Clear();
        _staleness = new ConfigurationChangePreviewStaleness(null, null);

        _repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _tasking = new Mock<ITaskingRepository>();
        _activities = new Mock<IActivityRepository>();
        _previews = new Mock<IConfigurationChangePreviewRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        _repository.Setup(r => r.Tasking).Returns(_tasking.Object);
        _repository.Setup(r => r.Activity).Returns(_activities.Object);
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previews.Object);

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR",
            SettingValues = [],
            ConnectorDefinition = new ConnectorDefinition { Name = "CSV" }
        };
        _connectedSystems.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false)).ReturnsAsync(connectedSystem);
        _connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, false)).ReturnsAsync(connectedSystem);
        _connectedSystems.Setup(r => r.GetConnectedSystemRunProfilesAsync(ConnectedSystemId)).ReturnsAsync(
        [
            new ConnectedSystemRunProfile { Id = FullSynchronisationProfileId, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation, ConnectedSystemId = ConnectedSystemId },
            new ConnectedSystemRunProfile { Id = DeltaSynchronisationProfileId, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation, ConnectedSystemId = ConnectedSystemId }
        ]);

        _activities.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activities.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _storedActivities.GetValueOrDefault(id));
        _activities.Setup(r => r.GetPreviewStalenessSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(() => _staleness);
        _previews.Setup(r => r.GetPreviewAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _storedPreviews.GetValueOrDefault(id));
        _tasking.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>())).Returns(Task.CompletedTask);

        _application = new JimApplication(_repository.Object);
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task CreateWorkerTaskAsync_FullSynchronisationCitingACompletedPreviewOfItsSystem_RecordsItOnTheRunActivityAsync()
    {
        var previewActivityId = StorePreview();

        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(previewActivityId));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(_createdActivities.Single().PreviewActivityId, Is.EqualTo(previewActivityId),
            "the run's Activity must name the preview the administrator read, or its audit trail cannot say what they were told");
    }

    [Test]
    public async Task CreateWorkerTaskAsync_FullSynchronisationCitingAnOutOfDatePreview_QueuesAndRecordsWhatOvertookItAsync()
    {
        // Staleness is judged across the whole deployment, so with any schedule running a preview is usually overtaken
        // before anyone acts on it (#2022). The run still queues and still names the preview the administrator read;
        // its Activity says the preview was out of date, and the caller is told, whichever surface it came from.
        var previewActivityId = StorePreview();
        var overtakingRun = new PreviewOvertakingActivity(Guid.CreateVersion7(), DateTime.UtcNow, ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetOperationType.Execute, "Delta Import", "HR Import");
        _staleness = new ConfigurationChangePreviewStaleness(overtakingRun, null);

        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(previewActivityId));

        var run = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(run.PreviewActivityId, Is.EqualTo(previewActivityId));
            Assert.That(run.PreviewOvertakenAt, Is.EqualTo(overtakingRun.Created));
            Assert.That(run.PreviewOvertakenBy, Does.Contain("Delta Import"));
            Assert.That(result.CitedPreviewWarning, Does.Contain("Delta Import"),
                "kept apart from other warnings, so a surface that has already asked about it need not say it twice");
        }
    }

    [Test]
    public async Task CreateWorkerTaskAsync_FullSynchronisationCitingACurrentPreview_RecordsNoStalenessAndWarnsOfNothingAsync()
    {
        var previewActivityId = StorePreview();

        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(previewActivityId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_createdActivities.Single().PreviewOvertakenAt, Is.Null);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.CitedPreviewWarning, Is.Null);
        }
    }

    [Test]
    public async Task CreateWorkerTaskAsync_WithoutAPreview_RecordsNoLinkAsync()
    {
        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(previewActivityId: null));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(_createdActivities.Single().PreviewActivityId, Is.Null,
            "a preview is an affordance, not a precondition: a run started without one records no link");
        _previews.Verify(r => r.GetPreviewAsync(It.IsAny<Guid>()), Times.Never,
            "a run citing nothing must not pay for a preview lookup");
    }

    [Test]
    public async Task CreateWorkerTaskAsync_PreviewOfAnotherConnectedSystem_RefusesTheRunAsync()
    {
        var previewActivityId = StorePreview(connectedSystemId: OtherConnectedSystemId);

        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(previewActivityId));

        AssertRefused(result, "not a Full Synchronisation preview of this Connected System");
    }

    [Test]
    public async Task CreateWorkerTaskAsync_PreviewOfAnotherKindOfChange_RefusesTheRunAsync()
    {
        var previewActivityId = StorePreview(surface: ConfigurationChangePreviewSurface.ConnectedSystemDeletion);

        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(previewActivityId));

        AssertRefused(result, "not a Full Synchronisation preview of this Connected System");
    }

    [Test]
    public async Task CreateWorkerTaskAsync_PreviewThatDoesNotExist_RefusesTheRunAsync()
    {
        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(Guid.NewGuid()));

        AssertRefused(result, "not a Full Synchronisation preview of this Connected System");
    }

    [TestCase(ConfigurationChangePreviewStageStatus.InProgress, TestName = "CreateWorkerTaskAsync_PreviewStillRunning_RefusesTheRunAsync")]
    [TestCase(ConfigurationChangePreviewStageStatus.Failed, TestName = "CreateWorkerTaskAsync_PreviewThatFailed_RefusesTheRunAsync")]
    [TestCase(ConfigurationChangePreviewStageStatus.Cancelled, TestName = "CreateWorkerTaskAsync_PreviewThatWasCancelled_RefusesTheRunAsync")]
    public async Task CreateWorkerTaskAsync_PreviewNotCompleted_RefusesTheRunAsync(ConfigurationChangePreviewStageStatus deltasStatus)
    {
        // A preview that has not finished has seen an arbitrary part of the population; recording it as what the
        // administrator was told would state a partial answer as the whole one.
        var previewActivityId = StorePreview(deltasStatus: deltasStatus);

        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(previewActivityId));

        AssertRefused(result, "has not completed");
    }

    [Test]
    public async Task CreateWorkerTaskAsync_PreviewBlockedByValidation_RefusesTheRunAsync()
    {
        // A blocked preview stopped before evaluating anything, so it says nothing about what the run would do.
        var previewActivityId = StorePreview(
            impactCountsStatus: ConfigurationChangePreviewStageStatus.NotApplicable,
            summaryStatus: ConfigurationChangePreviewStageStatus.NotApplicable,
            deltasStatus: ConfigurationChangePreviewStageStatus.NotApplicable);

        var result = await _application.Tasking.CreateWorkerTaskAsync(FullSynchronisationTask(previewActivityId));

        AssertRefused(result, "blocked");
    }

    [Test]
    public async Task CreateWorkerTaskAsync_DeltaSynchronisationCitingAFullSynchronisationPreview_RefusesTheRunAsync()
    {
        // The preview describes what a Full Synchronisation does; a Delta Synchronisation processes only what has
        // changed, so the preview would overstate it.
        var previewActivityId = StorePreview();
        var task = FullSynchronisationTask(previewActivityId);
        task.ConnectedSystemRunProfileId = DeltaSynchronisationProfileId;

        var result = await _application.Tasking.CreateWorkerTaskAsync(task);

        AssertRefused(result, "is not a Full Synchronisation");
    }

    private void AssertRefused(WorkerTaskCreationResult result, string reason)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False, "a run citing a preview that does not describe it must not queue");
            Assert.That(result.ErrorMessage, Does.Contain(reason));
            Assert.That(_createdActivities, Is.Empty, "a refused run must not leave a run Activity behind");
        }
        _tasking.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);
    }

    private Guid StorePreview(
        int connectedSystemId = ConnectedSystemId,
        ConfigurationChangePreviewSurface surface = ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation,
        ConfigurationChangePreviewStageStatus impactCountsStatus = ConfigurationChangePreviewStageStatus.Complete,
        ConfigurationChangePreviewStageStatus summaryStatus = ConfigurationChangePreviewStageStatus.Complete,
        ConfigurationChangePreviewStageStatus deltasStatus = ConfigurationChangePreviewStageStatus.Complete)
    {
        var activityId = Guid.CreateVersion7();
        _storedActivities[activityId] = new Activity
        {
            Id = activityId,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Preview,
            ConnectedSystemId = connectedSystemId
        };
        _storedPreviews[activityId] = new ConfigurationChangePreview
        {
            ActivityId = activityId,
            Surface = surface,
            ValidationStatus = ConfigurationChangePreviewStageStatus.Complete,
            ImpactCountsStatus = impactCountsStatus,
            SummaryStatus = summaryStatus,
            DeltasStatus = deltasStatus
        };
        return activityId;
    }

    private static SynchronisationWorkerTask FullSynchronisationTask(Guid? previewActivityId)
    {
        var task = SynchronisationWorkerTask.ForUser(ConnectedSystemId, FullSynchronisationProfileId, Guid.NewGuid(), "Ada Lovelace");
        task.PreviewActivityId = previewActivityId;
        return task;
    }
}
