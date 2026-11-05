// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A configuration change Activity's predicted counts beside what the applying run did (#2618), as the Activity read
/// assembles it: the reads that decide, and the one read of the run's own counts that only a surviving comparison makes.
/// </summary>
[TestFixture]
public class ActivityPredictionComparisonTests
{
    private const int SystemId = 3;
    private const int TypeId = 7;
    private const int RuleId = 11;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepo = null!;
    private JimApplication _jim = null!;

    private Guid _saveId;
    private Guid _previewId;
    private Guid _runId;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _previewRepo = new Mock<IConfigurationChangePreviewRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepo.Object);
        _jim = new JimApplication(_repo.Object);

        _saveId = Guid.NewGuid();
        _previewId = Guid.NewGuid();
        _runId = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private Activity Save(DateTime? overtakenAt = null) => new()
    {
        Id = _saveId,
        TargetType = ActivityTargetType.SynchronisationRule,
        PreviewActivityId = _previewId,
        PreviewOvertakenAt = overtakenAt
    };

    private ConfigurationChangeSynchronisationSummary Change(long id, Guid? claimedBy, DateTime? appliedAt) => new()
    {
        Id = id,
        ConnectedSystemId = SystemId,
        ConnectedSystemName = "HR",
        ConnectedSystemObjectTypeId = TypeId,
        ObjectTypeName = "user",
        SyncRuleId = RuleId,
        RequestedAt = DateTime.UtcNow.AddHours(-1),
        ClaimedByActivityId = claimedBy,
        AppliedAt = appliedAt
    };

    private void SaveRecorded(params ConfigurationChangeSynchronisationSummary[] changes) =>
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(_saveId, ObjectTypeAwaitingSynchronisation.MaxChangesListed))
            .ReturnsAsync(changes.ToList());

    private void RunClaimed(params ConfigurationChangeSynchronisationSummary[] changes) =>
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(_runId, ObjectTypeAwaitingSynchronisation.MaxChangesListed))
            .ReturnsAsync(changes.ToList());

    private void Preview(ConfigurationChangePreviewSurface surface = ConfigurationChangePreviewSurface.SynchronisationRuleScope, int? previewRuleId = RuleId)
    {
        _previewRepo.Setup(r => r.GetPreviewAsync(_previewId)).ReturnsAsync(new ConfigurationChangePreview
        {
            Surface = surface,
            ValidationStatus = ConfigurationChangePreviewStageStatus.Complete,
            ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete,
            SummaryStatus = ConfigurationChangePreviewStageStatus.Complete,
            DeltasStatus = ConfigurationChangePreviewStageStatus.Complete,
            ImpactCounts = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 40, SystemId)
            })
        });
        _activityRepo.Setup(r => r.GetActivityAsync(_previewId)).ReturnsAsync(new Activity { Id = _previewId, SyncRuleId = previewRuleId });
    }

    private void RunDid(int projected) =>
        _activityRepo.Setup(r => r.GetSyncOutcomeCountsForObjectTypeAsync(_runId, TypeId, It.IsAny<IReadOnlyCollection<ActivityRunProfileExecutionItemSyncOutcomeType>>()))
            .ReturnsAsync(new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>
            {
                [ActivityRunProfileExecutionItemSyncOutcomeType.Projected] = projected
            });

    private async Task<List<ActivityObjectTypeSynchronisation>> ReadAsync(Activity save) =>
        await _jim.ConnectedSystems.GetConfigurationChangeSynchronisationsForActivityAsync(save);

    [Test]
    public async Task Read_OneAppliedChangeInformedByAComparablePreview_ComparesPredictedWithWhatTheRunDidAsync()
    {
        var applied = Change(1, _runId, DateTime.UtcNow);
        SaveRecorded(applied);
        RunClaimed(applied);
        Preview();
        RunDid(projected: 38);

        var group = (await ReadAsync(Save())).Single();

        Assert.That(group.PredictionComparison!.Lines, Is.EqualTo(new[]
        {
            new ConfigurationChangePredictionLine(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 40, 38)
        }));
    }

    [Test]
    public async Task Read_RunAppliedTwoChangesToTheType_ShowsNoComparisonAndNeverReadsTheRunsCountsAsync()
    {
        var applied = Change(1, _runId, DateTime.UtcNow);
        SaveRecorded(applied);
        RunClaimed(applied, Change(2, _runId, DateTime.UtcNow));
        Preview();
        RunDid(projected: 38);

        var group = (await ReadAsync(Save())).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.PredictionComparison, Is.Null);
            _activityRepo.Verify(r => r.GetSyncOutcomeCountsForObjectTypeAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ActivityRunProfileExecutionItemSyncOutcomeType>>()), Times.Never);
        }
    }

    [Test]
    public async Task Read_ChangeStillAwaitingSynchronisation_ShowsNoComparisonAsync()
    {
        SaveRecorded(Change(1, claimedBy: null, appliedAt: null));
        Preview();

        Assert.That((await ReadAsync(Save())).Single().PredictionComparison, Is.Null);
    }

    [Test]
    public async Task Read_PreviewWasOvertaken_ShowsNoComparisonAsync()
    {
        var applied = Change(1, _runId, DateTime.UtcNow);
        SaveRecorded(applied);
        RunClaimed(applied);
        Preview();
        RunDid(projected: 38);

        Assert.That((await ReadAsync(Save(overtakenAt: DateTime.UtcNow))).Single().PredictionComparison, Is.Null);
    }

    [Test]
    public async Task Read_PreviewOfASurfaceWhoseCountsAreNotTheRunsCounts_ShowsNoComparisonAsync()
    {
        var applied = Change(1, _runId, DateTime.UtcNow);
        SaveRecorded(applied);
        RunClaimed(applied);
        Preview(surface: ConfigurationChangePreviewSurface.ObjectMatching);
        RunDid(projected: 38);

        Assert.That((await ReadAsync(Save())).Single().PredictionComparison, Is.Null);
    }

    [Test]
    public async Task Read_PreviewHasAgedOut_ShowsNoComparisonAsync()
    {
        var applied = Change(1, _runId, DateTime.UtcNow);
        SaveRecorded(applied);
        RunClaimed(applied);
        _previewRepo.Setup(r => r.GetPreviewAsync(_previewId)).ReturnsAsync((ConfigurationChangePreview?)null);

        Assert.That((await ReadAsync(Save())).Single().PredictionComparison, Is.Null);
    }

    [Test]
    public async Task Read_SaveHadNoPreview_ShowsNoComparisonAndReadsNoPreviewAsync()
    {
        var applied = Change(1, _runId, DateTime.UtcNow);
        SaveRecorded(applied);
        var save = Save();
        save.PreviewActivityId = null;

        Assert.That((await ReadAsync(save)).Single().PredictionComparison, Is.Null);
        _previewRepo.Verify(r => r.GetPreviewAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task Read_SynchronisationRun_NeverCarriesAComparisonAsync()
    {
        var run = new Activity { Id = _runId, TargetType = ActivityTargetType.ConnectedSystemRunProfile, PreviewActivityId = _previewId };
        var applied = Change(1, _runId, DateTime.UtcNow);
        RunClaimed(applied);
        Preview();
        RunDid(projected: 38);

        Assert.That((await ReadAsync(run)).Single().PredictionComparison, Is.Null);
    }
}
