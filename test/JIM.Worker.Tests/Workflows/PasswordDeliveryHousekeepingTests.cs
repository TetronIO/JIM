// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The worker's idle housekeeping noticing that queued password work has fallen due (#1119).
/// <para>
/// This is the trigger of last resort, and the only one that catches a retry. A password change that failed once
/// is scheduled to be tried again minutes later; no run profile, no administrator and no other password change
/// need happen in between, so without this the change would sit until something unrelated woke the queue.
/// </para>
/// </summary>
[TestFixture]
public class PasswordDeliveryHousekeepingTests
{
    private const int ConnectedSystemId = 9;

    private SyncRepository _syncRepository = null!;
    private JimApplication _jim = null!;
    private Worker _worker = null!;
    private List<WorkerTask> _createdWorkerTasks = null!;
    private bool _passAlreadyQueued;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _createdWorkerTasks = [];
        _passAlreadyQueued = false;

        var metaverseRepository = new Mock<IMetaverseRepository>();
        metaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([]);

        var activityRepository = new Mock<IActivityRepository>();
        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        // A recent cleanup stops the history retention path running during these tests.

        var serviceSettingsRepository = new Mock<IServiceSettingsRepository>();
        serviceSettingsRepository.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);

        var taskingRepository = new Mock<ITaskingRepository>();
        taskingRepository
            .Setup(r => r.HasQueuedPasswordDeliveryTaskAsync(It.IsAny<int?>()))
            .ReturnsAsync(() => _passAlreadyQueued);
        taskingRepository
            .Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => _createdWorkerTasks.Add(t))
            .Returns(Task.CompletedTask);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Metaverse).Returns(metaverseRepository.Object);
        repository.Setup(r => r.Activity).Returns(activityRepository.Object);
        repository.Setup(r => r.ServiceSettings).Returns(serviceSettingsRepository.Object);
        repository.Setup(r => r.Tasking).Returns(taskingRepository.Object);

        _syncRepository = new SyncRepository();
        _jim = new JimApplication(repository.Object, syncRepository: _syncRepository);

        _worker = new Worker(
            new Mock<IJimApplicationFactory>().Object,
            new Mock<IConnectorFactory>().Object,
            new Mock<IDbContextFactory<JimDbContext>>().Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        _worker?.Dispose();
    }

    /// <summary>
    /// Queues a change, optionally scheduled for a retry at some point in the future.
    /// </summary>
    private async Task QueueChangeAsync(DateTime? nextRetryAt = null)
    {
        var now = DateTime.UtcNow;
        await _syncRepository.QueuePasswordChangesAsync([
            new PendingPasswordChange
            {
                MetaverseObjectId = Guid.NewGuid(),
                ConnectedSystemId = ConnectedSystemId,
                EncryptedPassword = "$JIMPW$v1$ciphertext",
                CreatedAt = now,
                ExpiresAt = now.AddDays(7),
                NextRetryAt = nextRetryAt,
                ActivityId = Guid.NewGuid()
            }
        ]);
    }

    private List<PasswordDeliveryWorkerTask> RaisedDeliveryTasks =>
        _createdWorkerTasks.OfType<PasswordDeliveryWorkerTask>().ToList();

    [Test]
    public async Task PerformHousekeeping_WorkDue_AsksForADeliveryPassAsync()
    {
        await QueueChangeAsync();

        await _worker.PerformHousekeepingAsync(_jim);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RaisedDeliveryTasks, Has.Exactly(1).Items);
            Assert.That(RaisedDeliveryTasks[0].ConnectedSystemId, Is.Null,
                "Housekeeping is the clock, not a trigger that knows which system the work belongs to.");
            Assert.That(RaisedDeliveryTasks[0].InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        }
    }

    [Test]
    public async Task PerformHousekeeping_NothingDue_AsksForNothingAsync()
    {
        await _worker.PerformHousekeepingAsync(_jim);

        Assert.That(RaisedDeliveryTasks, Is.Empty, "A quiet idle tick must not fill the Operations queue with empty passes.");
    }

    [Test]
    public async Task PerformHousekeeping_RetryNotYetDue_AsksForNothingAsync()
    {
        await QueueChangeAsync(nextRetryAt: DateTime.UtcNow.AddHours(1));

        await _worker.PerformHousekeepingAsync(_jim);

        Assert.That(RaisedDeliveryTasks, Is.Empty, "A change waiting out its backoff is not yet work.");
    }

    [Test]
    public async Task PerformHousekeeping_RetryNowDue_AsksForADeliveryPassAsync()
    {
        // The case nothing else catches: a change that failed once, whose backoff has now elapsed.
        await QueueChangeAsync(nextRetryAt: DateTime.UtcNow.AddMinutes(-1));

        await _worker.PerformHousekeepingAsync(_jim);

        Assert.That(RaisedDeliveryTasks, Has.Exactly(1).Items);
    }

    [Test]
    public async Task PerformHousekeeping_PassAlreadyQueued_AsksForNothingAsync()
    {
        await QueueChangeAsync();
        _passAlreadyQueued = true;

        await _worker.PerformHousekeepingAsync(_jim);

        Assert.That(RaisedDeliveryTasks, Is.Empty);
    }
}
