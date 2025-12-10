using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Api.Tests;

[TestFixture]
public class SynchronisationControllerRunProfileTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application);
    }

    #region GetRunProfilesAsync tests

    [Test]
    public async Task GetRunProfilesAsync_WithValidConnectedSystem_ReturnsOkResult()
    {
        var connectedSystemId = 1;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile>());

        var result = await _controller.GetRunProfilesAsync(connectedSystemId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetRunProfilesAsync_WithNonExistentConnectedSystem_ReturnsNotFound()
    {
        var connectedSystemId = 999;
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.GetRunProfilesAsync(connectedSystemId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetRunProfilesAsync_WithRunProfiles_ReturnsRunProfileDtos()
    {
        var connectedSystemId = 1;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var runProfiles = new List<ConnectedSystemRunProfile>
        {
            new ConnectedSystemRunProfile
            {
                Id = 1,
                Name = "Full Import",
                ConnectedSystemId = connectedSystemId,
                RunType = ConnectedSystemRunType.FullImport,
                PageSize = 100
            },
            new ConnectedSystemRunProfile
            {
                Id = 2,
                Name = "Delta Import",
                ConnectedSystemId = connectedSystemId,
                RunType = ConnectedSystemRunType.DeltaImport,
                PageSize = 50
            }
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(runProfiles);

        var result = await _controller.GetRunProfilesAsync(connectedSystemId) as OkObjectResult;
        var dtos = result?.Value as IEnumerable<RunProfileDto>;

        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count(), Is.EqualTo(2));
        Assert.That(dtos.First().Name, Is.EqualTo("Full Import"));
        Assert.That(dtos.First().RunType, Is.EqualTo(ConnectedSystemRunType.FullImport));
    }

    [Test]
    public async Task GetRunProfilesAsync_MapsRunProfileFieldsCorrectly()
    {
        var connectedSystemId = 1;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var partition = new ConnectedSystemPartition { Id = 1, Name = "Default Partition" };
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = 10,
            Name = "Export",
            ConnectedSystemId = connectedSystemId,
            RunType = ConnectedSystemRunType.Export,
            PageSize = 200,
            Partition = partition,
            FilePath = "/data/export.csv"
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile> { runProfile });

        var result = await _controller.GetRunProfilesAsync(connectedSystemId) as OkObjectResult;
        var dtos = result?.Value as IEnumerable<RunProfileDto>;
        var dto = dtos?.First();

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(10));
        Assert.That(dto.Name, Is.EqualTo("Export"));
        Assert.That(dto.ConnectedSystemId, Is.EqualTo(connectedSystemId));
        Assert.That(dto.RunType, Is.EqualTo(ConnectedSystemRunType.Export));
        Assert.That(dto.PageSize, Is.EqualTo(200));
        Assert.That(dto.PartitionName, Is.EqualTo("Default Partition"));
        Assert.That(dto.FilePath, Is.EqualTo("/data/export.csv"));
    }

    [Test]
    public async Task GetRunProfilesAsync_WithEmptyRunProfiles_ReturnsEmptyList()
    {
        var connectedSystemId = 1;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile>());

        var result = await _controller.GetRunProfilesAsync(connectedSystemId) as OkObjectResult;
        var dtos = result?.Value as IEnumerable<RunProfileDto>;

        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count(), Is.EqualTo(0));
    }

    #endregion
}
