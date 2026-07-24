// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Verifies that every Activity completion path finalises the Activity's Run Profile execution
/// stat counters (#1078) before persisting the terminal status, so completed Activities always
/// serve their stats from exact, pre-computed counter rows instead of re-aggregating RPEIs.
/// </summary>
[TestFixture]
public class ActivityServerStatsFinalisationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;
    private List<string> _callOrder = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _callOrder = [];
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockActivityRepository
            .Setup(r => r.FinaliseActivityRunProfileExecutionStatsAsync(It.IsAny<Activity>()))
            .Callback(() => _callOrder.Add("finalise"))
            .Returns(Task.CompletedTask);
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback(() => _callOrder.Add("update"))
            .Returns(Task.CompletedTask);
        _mockRepository = new Mock<IRepository>();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    private static Activity NewInProgressActivity() => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        Status = ActivityStatus.InProgress,
        Executed = DateTime.UtcNow.AddMinutes(-1)
    };

    private void AssertFinalisedThenUpdated()
    {
        Assert.That(_callOrder, Is.EqualTo(new[] { "finalise", "update" }),
            "stats must be finalised before the terminal status is persisted so the flag lands in the same Activity update");
    }

    [Test]
    public async Task CompleteActivityAsync_FinalisesStatsBeforeUpdate()
    {
        await _application.Activities.CompleteActivityAsync(NewInProgressActivity());
        AssertFinalisedThenUpdated();
    }

    [Test]
    public async Task CompleteActivityWithWarningAsync_FinalisesStatsBeforeUpdate()
    {
        await _application.Activities.CompleteActivityWithWarningAsync(NewInProgressActivity());
        AssertFinalisedThenUpdated();
    }

    [Test]
    public async Task CompleteActivityWithErrorAsync_WithException_FinalisesStatsBeforeUpdate()
    {
        await _application.Activities.CompleteActivityWithErrorAsync(NewInProgressActivity(), new InvalidOperationException("boom"));
        AssertFinalisedThenUpdated();
    }

    [Test]
    public async Task CompleteActivityWithErrorAsync_WithMessage_FinalisesStatsBeforeUpdate()
    {
        await _application.Activities.CompleteActivityWithErrorAsync(NewInProgressActivity(), "boom");
        AssertFinalisedThenUpdated();
    }

    [Test]
    public async Task FailActivityWithErrorAsync_WithMessage_FinalisesStatsBeforeUpdate()
    {
        await _application.Activities.FailActivityWithErrorAsync(NewInProgressActivity(), "boom");
        AssertFinalisedThenUpdated();
    }

    [Test]
    public async Task FailActivityWithErrorAsync_WithException_FinalisesStatsBeforeUpdate()
    {
        await _application.Activities.FailActivityWithErrorAsync(NewInProgressActivity(), new InvalidOperationException("boom"));
        AssertFinalisedThenUpdated();
    }

    [Test]
    public async Task CancelActivityAsync_FinalisesStatsBeforeUpdate()
    {
        await _application.Activities.CancelActivityAsync(NewInProgressActivity());
        AssertFinalisedThenUpdated();
    }

    [Test]
    public async Task CancelActivityAsync_AlreadyCancelled_DoesNotFinaliseOrUpdate()
    {
        var activity = NewInProgressActivity();
        activity.Status = ActivityStatus.Cancelled;

        await _application.Activities.CancelActivityAsync(activity);

        Assert.That(_callOrder, Is.Empty);
    }

    [Test]
    public async Task CompleteActivityAsync_FinalisationFails_StillCompletesActivity()
    {
        // Finalisation failure must not leave the Activity stuck InProgress; the lazy
        // finalise-on-first-read path repairs the counters later.
        _mockActivityRepository
            .Setup(r => r.FinaliseActivityRunProfileExecutionStatsAsync(It.IsAny<Activity>()))
            .ThrowsAsync(new InvalidOperationException("stats aggregation failed"));

        var activity = NewInProgressActivity();
        await _application.Activities.CompleteActivityAsync(activity);

        Assert.That(_callOrder, Does.Contain("update"));
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
    }
}
