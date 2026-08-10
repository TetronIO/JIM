// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Preview;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for <see cref="ConfigurationChangePreviewServer"/>: the framework half of a configuration change preview,
/// exercised through a fake adapter so nothing here depends on a real surface existing.
///
/// Two invariants carry the weight. First, **a failed preview never presents partial results as complete**: an
/// evaluation that dies halfway through has seen an arbitrary subset of the population, and a summary computed from
/// it would be a wrong number stated with total confidence, which is worse than no number at all. Second, **group
/// counts are exact even when delta rows are capped**: capping decides what an administrator can drill into, never
/// what they are told the change would do.
/// </summary>
[TestFixture]
public class ConfigurationChangePreviewServerTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private Mock<ITaskingRepository> _taskingRepo = null!;
    private FakeBackgroundRunner _runner = null!;
    private List<WorkerTask> _queuedWorkerTasks = [];
    private JimApplication _jim = null!;
    private FakePreviewAdapter _adapter = null!;

    private Activity? _activity;
    private ConfigurationChangePreview? _preview;
    private List<ConfigurationChangePreviewGroup> _persistedGroups = [];

    private const int ObjectTypeId = 7;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _previewRepo = new Mock<IConfigurationChangePreviewRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _taskingRepo = new Mock<ITaskingRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);
        _repo.Setup(r => r.Tasking).Returns(_taskingRepo.Object);

        _queuedWorkerTasks = [];
        _runner = new FakeBackgroundRunner();
        _taskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => _queuedWorkerTasks.Add(t))
            .Returns(Task.CompletedTask);

        _activity = null;
        _preview = null;
        _persistedGroups = [];

        // Stand in for the database's key generation: EF assigns the Activity's Guid on insert, and the preview's
        // identity is that Guid, so nothing downstream works without it.
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                a.Id = Guid.CreateVersion7();
                _activity = a;
            })
            .Returns(Task.CompletedTask);

        _previewRepo.Setup(r => r.CreatePreviewAsync(It.IsAny<ConfigurationChangePreview>()))
            .Callback<ConfigurationChangePreview>(p => _preview = p)
            .Returns(Task.CompletedTask);

        _previewRepo.Setup(r => r.GetPreviewAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _preview is not null && _preview.ActivityId == id ? _preview : null);

        _previewRepo.Setup(r => r.CreatePreviewResultsAsync(It.IsAny<IReadOnlyCollection<ConfigurationChangePreviewGroup>>()))
            .Callback<IReadOnlyCollection<ConfigurationChangePreviewGroup>>(g => _persistedGroups = [.. g])
            .Returns(Task.CompletedTask);

        _activityRepo.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _activity is not null && _activity.Id == id ? _activity : null);

        _jim = new JimApplication(_repo.Object);
        _adapter = new FakePreviewAdapter();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private ConfigurationChangePreviewServer NewServer() =>
        new(_jim, new ConfigurationChangePreviewAdapterRegistry([_adapter])) { BackgroundRunner = _runner };

    /// <summary>
    /// Stores a threshold for the dispatch decision to read. Absent, the server falls back to its own default.
    /// </summary>
    private void SetWorkerThreshold(int threshold) =>
        _serviceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangePreviewWorkerThreshold))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangePreviewWorkerThreshold,
                ValueType = ServiceSettingValueType.Integer,
                DefaultValue = threshold.ToString()
            });

    private static ConfigurationChangePreviewRequest NewRequest(
        ConfigurationChangePreviewDeltaPersistence deltaPersistence = ConfigurationChangePreviewDeltaPersistence.Capped) => new()
    {
        DeltaPersistence = deltaPersistence,
        Surface = ConfigurationChangePreviewSurface.MetaverseObjectType,
        TargetId = ObjectTypeId,
        TargetName = "User",
        ProposedConfiguration = new FakeProposal("AllTriggersLost", 30),
        ProposedConfigurationSnapshot = """{"deletionRule":"AllTriggersLost"}""",
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedById = Guid.CreateVersion7(),
        InitiatedByName = "Ada Lovelace"
    };

    #region Stage 1: the proposal itself

    [Test]
    public async Task StartPreviewAsync_ValidProposal_RecordsPreviewActivityAndEstimateAsync()
    {
        _adapter.Estimate = new PreviewCostEstimate(1_200, 2);

        var result = await NewServer().StartPreviewAsync(NewRequest());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_activity, Is.Not.Null);
            Assert.That(result.ActivityId, Is.EqualTo(_activity!.Id));
            Assert.That(_activity!.TargetType, Is.EqualTo(ActivityTargetType.MetaverseObjectType),
                "A preview Activity must attach to the object it previewed, or it cannot be found from that object.");
            Assert.That(_activity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Preview),
                "A preview must never be mistakable for the change it was previewing.");
            Assert.That(_activity!.MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId));
            Assert.That(_activity!.TargetName, Is.EqualTo("User"));
            Assert.That(_preview, Is.Not.Null);
            Assert.That(_preview!.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.MetaverseObjectType));
            Assert.That(_preview!.ProposedConfigurationSnapshot, Is.EqualTo("""{"deletionRule":"AllTriggersLost"}"""));
            Assert.That(_preview!.ValidationStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(_preview!.EstimatedAffectedObjects, Is.EqualTo(1_200));
            Assert.That(_preview!.EstimatedDeltaRows, Is.EqualTo(2_400));
            Assert.That(result.IsBlocked, Is.False);
        }
    }

    [Test]
    public async Task StartPreviewAsync_Findings_ArePersistedForThePanelToReadAsync()
    {
        _adapter.Findings.Add(new PreviewValidationFinding(PreviewValidationSeverity.Warning, "No trigger systems are selected."));

        await NewServer().StartPreviewAsync(NewRequest());

        var stored = JsonSerializer.Deserialize<List<PreviewValidationFinding>>(_preview!.ValidationFindings!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Has.Count.EqualTo(1));
            Assert.That(stored![0].Message, Is.EqualTo("No trigger systems are selected."));
            Assert.That(stored![0].Severity, Is.EqualTo(PreviewValidationSeverity.Warning));
        }
    }

    [Test]
    public async Task StartPreviewAsync_BlockingFinding_StopsBeforeCostingOrEvaluatingAsync()
    {
        _adapter.Findings.Add(new PreviewValidationFinding(PreviewValidationSeverity.Blocking, "The grace period cannot be negative."));

        var result = await NewServer().StartPreviewAsync(NewRequest());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsBlocked, Is.True);
            Assert.That(_adapter.EstimateCalls, Is.Zero,
                "Costing a change that cannot be applied spends work on an answer nobody can use.");
            Assert.That(_preview!.ValidationStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete),
                "Validation found what it was asked to find; the proposal failed, not the stage.");
            Assert.That(_preview!.ImpactCountsStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.NotApplicable));
            Assert.That(_preview!.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.NotApplicable));
            Assert.That(_preview!.DeltasStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.NotApplicable));
            Assert.That(_activity!.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
        }
    }

    [Test]
    public async Task StartPreviewAsync_ValidationThrows_FailsTheWholePreviewAsync()
    {
        _adapter.ValidateThrows = new InvalidOperationException("the adapter could not read the current configuration");

        var result = await NewServer().StartPreviewAsync(NewRequest());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Failed, Is.True);
            Assert.That(_preview!.ValidationStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Failed));
            Assert.That(_preview!.HasFailed, Is.True);
            Assert.That(_activity!.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(_activity!.ErrorMessage, Does.Contain("could not read the current configuration"));
        }
    }

    #endregion

    #region Stage 2: counts

    [Test]
    public async Task RunPreviewAsync_CountOnlyAdapter_RecordsCountsAndSkipsEvaluationAsync()
    {
        _adapter.ProducesDeltas = false;
        _adapter.Counts.Add(new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 4_812));

        var server = NewServer();
        var request = NewRequest();
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, CancellationToken.None);

        var counts = JsonSerializer.Deserialize<List<PreviewImpactCount>>(_preview!.ImpactCounts!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts, Has.Count.EqualTo(1));
            Assert.That(counts![0].ObjectCount, Is.EqualTo(4_812));
            Assert.That(_preview!.ImpactCountsStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(_adapter.EvaluateCalls, Is.Zero);
            Assert.That(_preview!.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.NotApplicable),
                "An adapter that does not evaluate objects has not 'found nothing'; it has not looked.");
            Assert.That(_preview!.DeltasStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.NotApplicable));
            Assert.That(_preview!.IsComplete, Is.True);
            Assert.That(_activity!.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task RunPreviewAsync_CountingThrows_FailsWithoutEvaluatingAsync()
    {
        _adapter.CountThrows = new InvalidOperationException("count query failed");

        var server = NewServer();
        var request = NewRequest();
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_preview!.ImpactCountsStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Failed));
            Assert.That(_adapter.EvaluateCalls, Is.Zero);
            Assert.That(_preview!.HasFailed, Is.True);
            Assert.That(_preview!.IsComplete, Is.False);
            Assert.That(_activity!.Status, Is.EqualTo(ActivityStatus.FailedWithError));
        }
    }

    #endregion

    #region Stages 3 and 4: grouping, capping and progress

    [Test]
    public async Task RunPreviewAsync_DeltaStream_GroupsExactlyByTransitionAttributeAndValuePairAsync()
    {
        _adapter.Deltas.AddRange(
        [
            OutOfScope("Ada"), OutOfScope("Grace"), OutOfScope("Alan"),
            DomainChange("Ada"), DomainChange("Grace")
        ]);

        var server = NewServer();
        var request = NewRequest();
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, CancellationToken.None);

        // Three groups, not two: the two attribute-flow deltas carry different value pairs, and two pairs is well
        // inside the cardinality guard, so each is named rather than merged into one "Email changed" row.
        Assert.That(_persistedGroups, Has.Count.EqualTo(3));
        var scope = _persistedGroups.Single(g => g.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope);
        var flow = _persistedGroups.Where(g => g.AttributeName == "Email").ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.ObjectCount, Is.EqualTo(3));
            Assert.That(scope.Deltas, Has.Count.EqualTo(3));
            Assert.That(scope.DeltasSampled, Is.False);
            Assert.That(scope.MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId));
            Assert.That(scope.MetaverseObjectTypeName, Is.EqualTo("User"));
            Assert.That(scope.OldValue, Is.Null, "A transition with no attribute has no values to name.");
            Assert.That(flow.Sum(g => g.ObjectCount), Is.EqualTo(2));
            Assert.That(flow.Select(g => g.NewValue), Is.EquivalentTo(new[] { "Ada@new.example", "Grace@new.example" }));
            Assert.That(_persistedGroups[0].ObjectCount, Is.GreaterThanOrEqualTo(_persistedGroups[1].ObjectCount),
                "The landing view reads largest group first; ordering it at write time keeps every reader consistent.");
            Assert.That(_preview!.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(_preview!.DeltasStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(_preview!.DeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Full));
        }
    }

    [Test]
    public async Task RunPreviewAsync_GroupLargerThanTheCap_KeepsAnExactCountAndFlagsTheSampleAsync()
    {
        var overCap = ConfigurationChangePreviewServer.MaximumDeltasPerGroup + 5;
        for (var i = 0; i < overCap; i++)
            _adapter.Deltas.Add(OutOfScope($"User {i}"));

        var server = NewServer();
        var request = NewRequest();
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, CancellationToken.None);

        var group = _persistedGroups.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.ObjectCount, Is.EqualTo(overCap),
                "Capping decides what can be drilled into, never what the administrator is told the change would do.");
            Assert.That(group.Deltas, Has.Count.EqualTo(ConfigurationChangePreviewServer.MaximumDeltasPerGroup));
            Assert.That(group.DeltasSampled, Is.True);
            Assert.That(_preview!.DeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Capped));
        }
    }

    [Test]
    public async Task RunPreviewAsync_FullDataSetRequested_KeepsEveryDeltaAsync()
    {
        // The informed choice an administrator makes before a large preview runs has to actually change what is
        // kept. A "keep the full data set" option that still capped would be worse than not offering the choice:
        // they would go looking through a drill-down for objects it had silently dropped.
        var overCap = ConfigurationChangePreviewServer.MaximumDeltasPerGroup + 5;
        for (var i = 0; i < overCap; i++)
            _adapter.Deltas.Add(OutOfScope($"User {i}"));

        var server = NewServer();
        var request = NewRequest(ConfigurationChangePreviewDeltaPersistence.Full);
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, CancellationToken.None);

        var group = _persistedGroups.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.ObjectCount, Is.EqualTo(overCap));
            Assert.That(group.Deltas, Has.Count.EqualTo(overCap), "nothing was capped, so nothing may be missing");
            Assert.That(group.DeltasSampled, Is.False);
            Assert.That(_preview!.DeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Full));
        }
    }

    [Test]
    public async Task RunPreviewAsync_FullDataSetRequestedInAnotherProcess_IsHonouredFromThePreviewRowAsync()
    {
        // A preview dispatched to JIM.Worker is run from the persisted preview, so the choice has to survive on it
        // rather than in the request object the portal happened to be holding. Re-running with a default request
        // proves the row is what decides.
        var overCap = ConfigurationChangePreviewServer.MaximumDeltasPerGroup + 5;
        for (var i = 0; i < overCap; i++)
            _adapter.Deltas.Add(OutOfScope($"User {i}"));

        var server = NewServer();
        var start = await server.StartPreviewAsync(NewRequest(ConfigurationChangePreviewDeltaPersistence.Full));
        await server.RunPreviewAsync(start.ActivityId, NewRequest(), CancellationToken.None);

        Assert.That(_persistedGroups.Single().Deltas, Has.Count.EqualTo(overCap));
    }

    [Test]
    public async Task EstimatePreviewCostAsync_BeforeAnythingIsStarted_AnswersWithoutCreatingAnActivityAsync()
    {
        // The cap prompt has to know how big the answer would be *before* asking whether to keep all of it. An
        // estimate that created an Activity would leave one behind every time an administrator declined.
        _adapter.Estimate = new PreviewCostEstimate(120_000, 2);

        var estimate = await NewServer().EstimatePreviewCostAsync(NewRequest());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimate.EstimatedDeltaRows, Is.EqualTo(240_000L));
            Assert.That(_activity, Is.Null, "asking what a preview would cost is not asking for a preview");
            Assert.That(_preview, Is.Null);
        }
    }

    [Test]
    public async Task RunPreviewAsync_Deltas_DriveTheActivityColumnsTheProgressTriggerWatchesAsync()
    {
        _adapter.Deltas.AddRange([OutOfScope("Ada"), OutOfScope("Grace")]);
        _adapter.Estimate = new PreviewCostEstimate(2);

        var server = NewServer();
        var request = NewRequest();
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_activity!.ObjectsToProcess, Is.EqualTo(2));
            Assert.That(_activity!.ObjectsProcessed, Is.EqualTo(2),
                "Progress lives on the Activity because that is the only thing the notification trigger watches; " +
                "a preview that recorded progress only on its own row would leave the panel silent.");
            Assert.That(_activity!.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task RunPreviewAsync_EvaluationThrowsMidStream_PersistsNoPartialResultsAsync()
    {
        _adapter.Deltas.AddRange([OutOfScope("Ada"), OutOfScope("Grace"), OutOfScope("Alan")]);
        _adapter.ThrowAfterDeltas = 2;
        _adapter.EvaluateThrows = new InvalidOperationException("the evaluation lost its database connection");

        var server = NewServer();
        var request = NewRequest();
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_persistedGroups, Is.Empty,
                "Groups built from a partial stream would under-count without saying so, which is worse than no answer.");
            _previewRepo.Verify(r => r.CreatePreviewResultsAsync(It.IsAny<IReadOnlyCollection<ConfigurationChangePreviewGroup>>()), Times.Never);
            Assert.That(_preview!.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Failed));
            Assert.That(_preview!.DeltasStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Failed));
            Assert.That(_preview!.IsComplete, Is.False);
            Assert.That(_activity!.Status, Is.EqualTo(ActivityStatus.FailedWithError));
        }
    }

    [Test]
    public async Task RunPreviewAsync_Cancelled_StopsEvaluatingAndRecordsCancellationAsync()
    {
        for (var i = 0; i < 50; i++)
            _adapter.Deltas.Add(OutOfScope($"User {i}"));

        using var cancellation = new CancellationTokenSource();
        _adapter.OnDeltaYielded = yielded =>
        {
            if (yielded == 10)
                cancellation.Cancel();
        };

        var server = NewServer();
        var request = NewRequest();
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_adapter.DeltasYielded, Is.LessThan(50), "A cancelled preview must stop evaluating, not merely stop reporting.");
            Assert.That(_persistedGroups, Is.Empty);
            Assert.That(_preview!.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Cancelled));
            Assert.That(_preview!.DeltasStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Cancelled));
            Assert.That(_preview!.HasFailed, Is.False, "Nothing went wrong; the administrator changed their mind.");
            Assert.That(_activity!.Status, Is.EqualTo(ActivityStatus.Cancelled));
        }
    }

    [Test]
    public async Task RunPreviewAsync_NoObjectsAffected_CompletesWithAnEmptySummaryAsync()
    {
        var server = NewServer();
        var request = NewRequest();
        var start = await server.StartPreviewAsync(request);
        await server.RunPreviewAsync(start.ActivityId, request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_adapter.EvaluateCalls, Is.EqualTo(1));
            Assert.That(_preview!.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete),
                "An adapter that looked and found nothing has answered the question; that is not the same as not looking.");
            Assert.That(_preview!.DeltasStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(_preview!.IsComplete, Is.True);
            Assert.That(_activity!.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    #endregion

    #region Guards

    [Test]
    public void StartPreviewAsync_SurfaceWithNoAdapter_ThrowsRatherThanReturningNothingAsync()
    {
        var request = new ConfigurationChangePreviewRequest
        {
            Surface = ConfigurationChangePreviewSurface.ConnectedSystem,
            TargetId = 1,
            ProposedConfiguration = new object(),
            InitiatedByType = ActivityInitiatorType.System
        };

        Assert.That(async () => await NewServer().StartPreviewAsync(request),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public async Task RunPreviewAsync_RequestForADifferentSurface_ThrowsAsync()
    {
        var server = NewServer();
        var start = await server.StartPreviewAsync(NewRequest());
        var mismatched = new ConfigurationChangePreviewRequest
        {
            Surface = ConfigurationChangePreviewSurface.ConnectedSystem,
            TargetId = 1,
            ProposedConfiguration = new object(),
            InitiatedByType = ActivityInitiatorType.System
        };

        Assert.That(async () => await server.RunPreviewAsync(start.ActivityId, mismatched, CancellationToken.None),
            Throws.InstanceOf<InvalidOperationException>());
    }

    #endregion

    #region Dispatch

    [Test]
    public async Task StartAndDispatchPreviewAsync_SmallProposal_RunsInThisProcessAsync()
    {
        _adapter.Estimate = new PreviewCostEstimate(10);

        var result = await NewServer().StartAndDispatchPreviewAsync(NewRequest());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_runner.Enqueued.Select(e => e.ActivityId), Is.EqualTo(new[] { result.ActivityId }));
            Assert.That(_queuedWorkerTasks, Is.Empty,
                "A preview of ten objects finishes in the time it takes the worker to notice it exists.");
            Assert.That(_preview!.DispatchedToWorker, Is.False);
        }
    }

    [Test]
    public async Task StartAndDispatchPreviewAsync_LargeProposal_GoesToTheWorkerAsync()
    {
        _adapter.Estimate = new PreviewCostEstimate(5_000);

        var result = await NewServer().StartAndDispatchPreviewAsync(NewRequest());

        var queued = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_runner.Enqueued, Is.Empty);
            Assert.That(queued.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.MetaverseObjectType));
            Assert.That(queued.TargetId, Is.EqualTo(ObjectTypeId));
            Assert.That(queued.TargetName, Is.EqualTo("User"));
            Assert.That(queued.InitiatedByName, Is.EqualTo("Ada Lovelace"));
            Assert.That(queued.Activity?.Id, Is.EqualTo(result.ActivityId),
                "The task must attach to the Activity validation already ran under, not create a second one.");
            Assert.That(_preview!.DispatchedToWorker, Is.True);
        }
    }

    [Test]
    public async Task StartAndDispatchPreviewAsync_LargeProposal_QueuesAProposalTheWorkerCanReconstructAsync()
    {
        // The whole reason an adapter declares a proposal type: a proposal is an unsaved object, and crossing a
        // process boundary is the one thing it cannot do by itself.
        _adapter.Estimate = new PreviewCostEstimate(5_000);

        await NewServer().StartAndDispatchPreviewAsync(NewRequest());

        var queued = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().Single();
        var rehydrated = JsonSerializer.Deserialize<FakeProposal>(queued.ProposedConfigurationPayload);

        Assert.That(rehydrated, Is.EqualTo(new FakeProposal("AllTriggersLost", 30)));
    }

    [Test]
    public async Task StartAndDispatchPreviewAsync_NoInProcessRunner_GoesToTheWorkerWhateverTheSizeAsync()
    {
        // JIM.Worker and JIM.Scheduler register no runner. A preview started there is slower than it needs to be,
        // never wrong, which is the right way round for a fallback.
        _adapter.Estimate = new PreviewCostEstimate(1);
        var server = NewServer();
        server.BackgroundRunner = null;

        await server.StartAndDispatchPreviewAsync(NewRequest());

        Assert.That(_queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task StartAndDispatchPreviewAsync_ThresholdRaised_KeepsALargerPreviewInThisProcessAsync()
    {
        SetWorkerThreshold(10_000);
        _adapter.Estimate = new PreviewCostEstimate(5_000);

        await NewServer().StartAndDispatchPreviewAsync(NewRequest());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_runner.Enqueued, Has.Count.EqualTo(1),
                "The threshold is a service setting so it can be tuned; a dispatch that ignored it would make that a lie.");
            Assert.That(_queuedWorkerTasks, Is.Empty);
        }
    }

    [Test]
    public async Task StartAndDispatchPreviewAsync_BlockingFinding_DispatchesNothingAsync()
    {
        _adapter.Findings.Add(new PreviewValidationFinding(PreviewValidationSeverity.Blocking, "The grace period cannot be negative."));

        await NewServer().StartAndDispatchPreviewAsync(NewRequest());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_runner.Enqueued, Is.Empty);
            Assert.That(_queuedWorkerTasks, Is.Empty,
                "There is nothing to evaluate about a change that cannot be applied.");
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Stands in for a surface's own update type. Named rather than anonymous because a proposal has to survive a
    /// JSON round trip to reach JIM.Worker, and an anonymous type would let a test pass that dispatch could not.
    /// </summary>
    private record FakeProposal(string DeletionRule, int GracePeriodDays);

    /// <summary>
    /// Stands in for JIM.Web's in-process runner. It records rather than runs: what matters at this level is which
    /// path a preview was sent down, not what happened once it got there.
    /// </summary>
    private sealed class FakeBackgroundRunner : IConfigurationChangePreviewBackgroundRunner
    {
        public List<(Guid ActivityId, ConfigurationChangePreviewRequest Request)> Enqueued { get; } = [];

        public List<Guid> Cancelled { get; } = [];

        public void Enqueue(Guid activityId, ConfigurationChangePreviewRequest request) => Enqueued.Add((activityId, request));

        public bool Cancel(Guid activityId)
        {
            Cancelled.Add(activityId);
            return Enqueued.Any(e => e.ActivityId == activityId);
        }
    }

    private static PreviewDelta OutOfScope(string name) => new(
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope,
        ObjectDisplayName: name,
        ObjectTypeName: "User",
        MetaverseObjectTypeId: ObjectTypeId,
        MetaverseObjectId: Guid.CreateVersion7());

    private static PreviewDelta DomainChange(string name) => new(
        ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
        ObjectDisplayName: name,
        ObjectTypeName: "User",
        MetaverseObjectTypeId: ObjectTypeId,
        MetaverseObjectId: Guid.CreateVersion7(),
        AttributeName: "Email",
        OldValue: $"{name}@old.example",
        NewValue: $"{name}@new.example");

    /// <summary>
    /// A preview adapter with no surface behind it: every stage is whatever the test says it is. The framework is
    /// meant to work without knowing anything about the surface it is previewing, so its tests should not need one
    /// either.
    /// </summary>
    private sealed class FakePreviewAdapter : IConfigurationChangePreviewAdapter
    {
        public ConfigurationChangePreviewSurface Surface { get; init; } = ConfigurationChangePreviewSurface.MetaverseObjectType;
        public bool ProducesDeltas { get; set; } = true;
        public Type ProposalType => typeof(FakeProposal);

        public List<PreviewValidationFinding> Findings { get; } = [];
        public PreviewCostEstimate Estimate { get; set; } = new(0);
        public List<PreviewImpactCount> Counts { get; } = [];
        public List<PreviewDelta> Deltas { get; } = [];

        public Exception? ValidateThrows { get; set; }
        public Exception? CountThrows { get; set; }
        public Exception? EvaluateThrows { get; set; }
        public int ThrowAfterDeltas { get; set; } = int.MaxValue;
        public Action<int>? OnDeltaYielded { get; set; }

        public int ValidateCalls { get; private set; }
        public int EstimateCalls { get; private set; }
        public int CountCalls { get; private set; }
        public int EvaluateCalls { get; private set; }
        public int DeltasYielded { get; private set; }

        public Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
        {
            ValidateCalls++;
            if (ValidateThrows is not null)
                throw ValidateThrows;
            return Task.FromResult(new List<PreviewValidationFinding>(Findings));
        }

        public Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
        {
            EstimateCalls++;
            return Task.FromResult(Estimate);
        }

        public Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context)
        {
            CountCalls++;
            if (CountThrows is not null)
                throw CountThrows;
            return Task.FromResult(new List<PreviewImpactCount>(Counts));
        }

        public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            EvaluateCalls++;
            foreach (var delta in Deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DeltasYielded >= ThrowAfterDeltas && EvaluateThrows is not null)
                    throw EvaluateThrows;

                yield return delta;
                DeltasYielded++;
                OnDeltaYielded?.Invoke(DeltasYielded);
                await Task.Yield();
            }
        }
    }

    #endregion
}
