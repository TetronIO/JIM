// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Operations;
using JIM.Worker;
using Moq;

namespace JIM.Worker.Tests;

/// <summary>
/// How a run that was short of memory says so on its Activity once it ends, and the words the Worker's memory guard
/// uses for each of its decisions.
/// </summary>
[TestFixture]
public class WorkerMemoryOutcomeTests
{
    private const long Gb = 1_000_000_000;
    private Mock<IActivityRepository> _activities = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        var repository = new Mock<IRepository>();
        _activities = new Mock<IActivityRepository>();
        repository.Setup(r => r.Activity).Returns(_activities.Object);
        _jim = new JimApplication(repository.Object);
    }

    [TearDown]
    public void TearDown() => _jim.Dispose();

    [Test]
    public async Task AddWarningAsync_CompletedActivity_BecomesCompleteWithWarningCarryingItAsync()
    {
        var activity = new Activity { Status = ActivityStatus.Complete };

        await WorkerMemoryOutcome.AddWarningAsync(_jim, activity, "Memory was low.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.WarningMessage, Is.EqualTo("Memory was low."));
            _activities.Verify(a => a.UpdateActivityAsync(activity), Times.Once);
        }
    }

    [Test]
    public async Task AddWarningAsync_ActivityAlreadyWarning_KeepsWhatItSaidAndAddsThisAsync()
    {
        var activity = new Activity { Status = ActivityStatus.CompleteWithWarning, WarningMessage = "Deletions were not detected in People." };

        await WorkerMemoryOutcome.AddWarningAsync(_jim, activity, "Memory was low.");

        Assert.That(activity.WarningMessage, Is.EqualTo("Deletions were not detected in People.\n\nMemory was low."));
    }

    [Test]
    public async Task AddWarningAsync_FailedActivity_StaysFailedAndCarriesTheWarningAsync()
    {
        var activity = new Activity { Status = ActivityStatus.FailedWithError, ErrorMessage = "The directory refused the search." };

        await WorkerMemoryOutcome.AddWarningAsync(_jim, activity, "Memory was low.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError), "a warning never softens a failure");
            Assert.That(activity.WarningMessage, Is.EqualTo("Memory was low."));
        }
    }

    [Test]
    public void Texts_NameTheFiguresTheShareTheWorkerStopsAtAndTheRemedy()
    {
        var reading = new MemoryReading { ProcessBytes = 41 * Gb, TotalBytes = 62 * Gb, AvailableBytes = 5 * Gb };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WorkerMemoryOutcome.WarningText(reading), Does.Contain("57 GB of 62 GB in use, 41 GB of it by the Worker").And.Contain("92%").And.Contain("give JIM more memory"));
            Assert.That(WorkerMemoryOutcome.StopText(reading), Does.Contain("57 GB of 62 GB in use, 41 GB of it by the Worker").And.Contain("give JIM more memory").IgnoreCase);
            Assert.That(WorkerMemoryOutcome.AbortText(reading), Does.Contain("57 GB of 62 GB in use, 41 GB of it by the Worker").And.Contain("restarted"));
        }
    }
}
