// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging;
using Moq;

namespace JIM.Worker.Tests;

/// <summary>
/// A Run Profile disabled after its task was queued does not run (#2740). The queue refuses a disabled Run Profile, but a
/// Schedule queues all of its steps when it starts, so a Run Profile disabled meanwhile (by an administrator, or by a
/// hierarchy refresh that removed its Partition) reaches the Worker anyway. The Worker refuses it there, failing the run's
/// Activity with the reason, never silently.
/// </summary>
[TestFixture]
public class WorkerDisabledRunProfileTests
{
    private Mock<IActivityRepository> _activityRepository = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _activityRepository = new Mock<IActivityRepository>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _jim = new JimApplication(repository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    [Test]
    public async Task RefuseIfRunProfileDisabledAsync_DisabledSinceItWasQueued_FailsTheActivityWithTheReasonAsync()
    {
        var activity = new Activity { Status = ActivityStatus.InProgress, Executed = DateTime.UtcNow };
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = 10,
            Name = "Full Import (Glitterband)",
            RunType = ConnectedSystemRunType.FullImport,
            Enabled = false,
            DisabledReason = ConnectedSystemRunProfileDisabledReason.PartitionRemoved,
            DisabledPartitionName = "dc=glitterband,dc=local"
        };

        var refused = await Worker.RefuseIfRunProfileDisabledAsync(_jim, activity, runProfile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.True, "the run must not go ahead");
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError), "a refused run fails its Activity, never silently");
            Assert.That(activity.ErrorMessage, Is.EqualTo(RunProfileEnablement.DescribeRunRefusal(runProfile)));
        }

        _activityRepository.Verify(r => r.UpdateActivityAsync(activity), Times.Once, "the failure is recorded");
    }

    [Test]
    public async Task RefuseIfRunProfileDisabledAsync_Enabled_LetsTheRunGoAheadAsync()
    {
        var activity = new Activity { Status = ActivityStatus.InProgress };
        var runProfile = new ConnectedSystemRunProfile { Id = 10, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport };

        var refused = await Worker.RefuseIfRunProfileDisabledAsync(_jim, activity, runProfile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.InProgress));
        }

        _activityRepository.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }
}
