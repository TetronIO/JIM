// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class ConnectedSystemServerChangeHistoryTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _jim = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    [Test]
    public async Task GetCsoChangeHistoryAsync_DelegatesToRepositoryAsync()
    {
        var csoId = Guid.NewGuid();
        var expected = (
            new List<CsoChangeHistoryDto>
            {
                new CsoChangeHistoryDto { Id = Guid.NewGuid(), ChangeTime = DateTime.UtcNow, InitiatedByName = "Sync" }
            },
            TotalCount: 1);
        _mockCsRepo
            .Setup(r => r.GetCsoChangeHistoryAsync(csoId, 2, 25))
            .ReturnsAsync(expected);

        var (items, total) = await _jim.ConnectedSystems.GetCsoChangeHistoryAsync(csoId, 2, 25);

        Assert.That(total, Is.EqualTo(1));
        Assert.That(items, Has.Count.EqualTo(1));
        _mockCsRepo.Verify(r => r.GetCsoChangeHistoryAsync(csoId, 2, 25), Times.Once);
    }

    [Test]
    public async Task GetCsoChangeHistoryAsync_ClampsPageSizeAboveLimitAsync()
    {
        var csoId = Guid.NewGuid();
        _mockCsRepo
            .Setup(r => r.GetCsoChangeHistoryAsync(csoId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<CsoChangeHistoryDto>(), 0));

        // Caller asks for 1000 — server should clamp to 100.
        await _jim.ConnectedSystems.GetCsoChangeHistoryAsync(csoId, 1, 1000);

        _mockCsRepo.Verify(r => r.GetCsoChangeHistoryAsync(csoId, 1, 100), Times.Once);
    }

    [Test]
    public async Task GetCsoChangeHistoryAsync_FloorsZeroAndNegativeArgumentsAsync()
    {
        var csoId = Guid.NewGuid();
        _mockCsRepo
            .Setup(r => r.GetCsoChangeHistoryAsync(csoId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<CsoChangeHistoryDto>(), 0));

        // Caller asks for page 0, pageSize -5 — server should floor to 1, 1.
        await _jim.ConnectedSystems.GetCsoChangeHistoryAsync(csoId, 0, -5);

        _mockCsRepo.Verify(r => r.GetCsoChangeHistoryAsync(csoId, 1, 1), Times.Once);
    }
}
