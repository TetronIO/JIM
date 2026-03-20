using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Expressions;
using JIM.Models.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Security;
using JIM.Models.Staging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class SynchronisationControllerRunProfileTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private IExpressionEvaluator _expressionEvaluator = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private readonly Guid _testApiKeyId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(_testApiKeyId))
            .ReturnsAsync(new JIM.Models.Security.ApiKey { Id = _testApiKeyId, Name = "TestApiKey" });
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        // Set up API key authentication context for the controller
        var claims = new List<Claim>
        {
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, _testApiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
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
        var dtosList = dtos!.ToList();
        Assert.That(dtosList.Count, Is.EqualTo(2));
        Assert.That(dtosList.First().Name, Is.EqualTo("Full Import"));
        Assert.That(dtosList.First().RunType, Is.EqualTo(ConnectedSystemRunType.FullImport));
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

    #region UpdateRunProfileAsync tests

    [Test]
    public async Task UpdateRunProfileAsync_WithValidRequest_ReturnsOkWithUpdatedDto()
    {
        var connectedSystemId = 1;
        var runProfileId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = runProfileId,
            Name = "Old Name",
            ConnectedSystemId = connectedSystemId,
            RunType = ConnectedSystemRunType.FullImport,
            PageSize = 100,
            FilePath = "/data/old.csv"
        };
        var request = new UpdateRunProfileRequest
        {
            Name = "New Name",
            PageSize = 200,
            FilePath = "/data/new.csv"
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile> { runProfile });

        var result = await _controller.UpdateRunProfileAsync(connectedSystemId, runProfileId, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var dto = okResult.Value as RunProfileDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Name, Is.EqualTo("New Name"));
        Assert.That(dto.PageSize, Is.EqualTo(200));
        Assert.That(dto.FilePath, Is.EqualTo("/data/new.csv"));
    }

    [Test]
    public async Task UpdateRunProfileAsync_WithNonExistentConnectedSystem_ReturnsNotFound()
    {
        var connectedSystemId = 999;
        var request = new UpdateRunProfileRequest { Name = "Updated" };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.UpdateRunProfileAsync(connectedSystemId, 1, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateRunProfileAsync_WithNonExistentRunProfile_ReturnsNotFound()
    {
        var connectedSystemId = 1;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var request = new UpdateRunProfileRequest { Name = "Updated" };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile>());

        var result = await _controller.UpdateRunProfileAsync(connectedSystemId, 999, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateRunProfileAsync_WithPartialUpdate_OnlyUpdatesProvidedFields()
    {
        var connectedSystemId = 1;
        var runProfileId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = runProfileId,
            Name = "Original Name",
            ConnectedSystemId = connectedSystemId,
            RunType = ConnectedSystemRunType.FullImport,
            PageSize = 100,
            FilePath = "/data/original.csv"
        };
        var request = new UpdateRunProfileRequest
        {
            Name = "Updated Name"
            // PageSize and FilePath not set — should remain unchanged
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile> { runProfile });

        var result = await _controller.UpdateRunProfileAsync(connectedSystemId, runProfileId, request) as OkObjectResult;
        var dto = result?.Value as RunProfileDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Name, Is.EqualTo("Updated Name"));
        Assert.That(dto.PageSize, Is.EqualTo(100));
        Assert.That(dto.FilePath, Is.EqualTo("/data/original.csv"));
    }

    [Test]
    public async Task UpdateRunProfileAsync_CallsRepositoryUpdateAsync()
    {
        var connectedSystemId = 1;
        var runProfileId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = runProfileId,
            Name = "Test Profile",
            ConnectedSystemId = connectedSystemId,
            RunType = ConnectedSystemRunType.FullImport,
            PageSize = 100
        };
        var request = new UpdateRunProfileRequest { Name = "Updated" };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile> { runProfile });

        await _controller.UpdateRunProfileAsync(connectedSystemId, runProfileId, request);

        _mockConnectedSystemRepo.Verify(
            r => r.UpdateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp => rp.Name == "Updated")),
            Times.Once);
    }

    [Test]
    public async Task UpdateRunProfileAsync_WithInvalidPartitionId_ReturnsBadRequest()
    {
        var connectedSystemId = 1;
        var runProfileId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = runProfileId,
            Name = "Test Profile",
            ConnectedSystemId = connectedSystemId,
            RunType = ConnectedSystemRunType.FullImport,
            PageSize = 100
        };
        var request = new UpdateRunProfileRequest { PartitionId = 999 };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(connectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile> { runProfile });
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemPartitionsAsync(It.IsAny<ConnectedSystem>()))
            .ReturnsAsync(new List<ConnectedSystemPartition>());

        var result = await _controller.UpdateRunProfileAsync(connectedSystemId, runProfileId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion
}
