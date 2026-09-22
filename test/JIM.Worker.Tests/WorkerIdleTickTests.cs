// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests;

/// <summary>
/// The main loop's JimApplication lives for the Worker's lifetime on a change-tracking DbContext, and every Worker
/// Task and Activity it dequeues or checks for cancellation stays tracked in it. Nothing in the loop needs those
/// copies past the iteration that loaded them (dispatch re-reads the task on its own context, heartbeats are direct
/// updates, cancellation re-loads by id), so an idle tick must release them, or a busy Worker's memory grows with
/// every task it has ever run. Housekeeping itself runs on a context of its own (see
/// <see cref="Workflows.HousekeepingActivityWorkflowTests"/>), so the tick's clear is what bounds the main loop.
/// </summary>
[TestFixture]
public class WorkerIdleTickTests
{
    private Mock<IRepository> _mainLoopRepository = null!;
    private Mock<ISyncRepository> _mainLoopSyncRepository = null!;
    private Mock<IMetaverseRepository> _housekeepingMetaverseRepository = null!;
    private JimApplication _mainLoopJim = null!;
    private JimApplication _housekeepingJim = null!;
    private Worker _worker = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        // The main loop's own instance: the one whose tracker must be released.
        _mainLoopRepository = new Mock<IRepository>();
        _mainLoopSyncRepository = new Mock<ISyncRepository>();
        _mainLoopJim = new JimApplication(_mainLoopRepository.Object, syncRepository: _mainLoopSyncRepository.Object);

        // The instance the factory hands housekeeping; nothing is eligible, so the tick is otherwise quiet.
        _housekeepingMetaverseRepository = new Mock<IMetaverseRepository>();
        _housekeepingMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([]);
        var housekeepingRepository = new Mock<IRepository>();
        housekeepingRepository.Setup(r => r.Metaverse).Returns(_housekeepingMetaverseRepository.Object);
        _housekeepingJim = new JimApplication(housekeepingRepository.Object, syncRepository: new SyncRepository());

        var jimFactory = new Mock<IJimApplicationFactory>();
        jimFactory.Setup(f => f.Create()).Returns(() => _housekeepingJim);

        _worker = new Worker(
            jimFactory.Object,
            new Mock<IConnectorFactory>().Object,
            new Mock<IDbContextFactory<JimDbContext>>().Object);
    }

    [TearDown]
    public void TearDown()
    {
        _mainLoopJim.Dispose();
        _housekeepingJim.Dispose();
        _worker.Dispose();
    }

    [Test]
    public async Task PerformIdleTick_Always_ReleasesEverythingTheMainLoopContextTrackedAsync()
    {
        await _worker.PerformIdleTickAsync(_mainLoopJim);

        _mainLoopSyncRepository.Verify(r => r.ClearChangeTracker(), Times.Once,
            "An idle tick must release the Worker Tasks and Activities the main loop's context has tracked since the last one");
    }

    [Test]
    public async Task PerformIdleTick_Always_RunsHousekeepingOnItsOwnInstanceFirstAsync()
    {
        await _worker.PerformIdleTickAsync(_mainLoopJim);

        _housekeepingMetaverseRepository.Verify(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()), Times.Once,
            "The idle tick still performs housekeeping, on the factory's instance rather than the main loop's");
        _mainLoopRepository.Verify(r => r.Metaverse, Times.Never,
            "Housekeeping must not read through the main loop's context");
    }
}
