// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for the crash recovery mechanism that unsticks Pending Exports left in Status Executing by a
/// worker crash or restart mid-export, mirroring <see cref="StaleTaskRecoveryTests"/> for the analogous
/// stale worker task recovery. Worker.ExecuteAsync calls this once at startup, alongside
/// RecoverStaleWorkerTasksAsync; that call site itself is not unit tested here for the same reason the
/// existing stale-task recovery's call site is not (see the class remarks): ExecuteAsync is a single
/// BackgroundService method requiring a fully mocked database initialisation, CSO cache warm and
/// heartbeat writer before reaching either recovery call, and no existing Worker test builds that
/// harness. The Application-layer method this delegates to is exercised directly here instead.
/// </summary>
[TestFixture]
public class PendingExportCrashRecoveryTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISyncRepository> _mockSyncRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _mockRepository = new Mock<IRepository>();
        _mockSyncRepository = new Mock<ISyncRepository>();
        _application = new JimApplication(_mockRepository.Object, syncRepository: _mockSyncRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task RecoverStrandedExecutingPendingExportsAsync_DelegatesToTheRepositoryAndReturnsItsCountAsync()
    {
        _mockSyncRepository
            .Setup(r => r.RecoverStrandedExecutingPendingExportsAsync())
            .ReturnsAsync(2);

        var recoveredCount = await _application.ExportExecution.RecoverStrandedExecutingPendingExportsAsync();

        Assert.That(recoveredCount, Is.EqualTo(2));
        _mockSyncRepository.Verify(r => r.RecoverStrandedExecutingPendingExportsAsync(), Times.Once);
    }

    [Test]
    public async Task RecoverStrandedExecutingPendingExportsAsync_NothingStranded_ReturnsZeroAsync()
    {
        _mockSyncRepository
            .Setup(r => r.RecoverStrandedExecutingPendingExportsAsync())
            .ReturnsAsync(0);

        var recoveredCount = await _application.ExportExecution.RecoverStrandedExecutingPendingExportsAsync();

        Assert.That(recoveredCount, Is.EqualTo(0));
    }
}
