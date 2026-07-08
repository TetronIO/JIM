// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.ExampleData;
using JIM.Models.Security;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for ExampleDataController's Example Data Set CRUD endpoints (issue #154, Phase 2).
/// </summary>
[TestFixture]
public class ExampleDataControllerExampleDataSetCrudTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IExampleDataRepository> _mockExampleDataRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<ExampleDataController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private ExampleDataController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockExampleDataRepo = new Mock<IExampleDataRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ExampleData).Returns(_mockExampleDataRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        // Every write path now records a configuration-change Activity (see ExampleDataServer); stub the two calls it
        // always makes so these CRUD tests keep exercising just the repository mutation they assert on, without
        // asserting on Activity attribution themselves. The dedicated change-history read tests live in
        // ExampleDataChangeHistoryApiTests.
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockLogger = new Mock<ILogger<ExampleDataController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new ExampleDataController(_mockLogger.Object, _application);

        // Authenticate as an API key so the base controller resolves a non-null principal for Activity attribution.
        // The write mutators attribute the recorded Activity to this principal; a null principal would fail the
        // Activity's mandatory-attribution validation. Mirrors SynchronisationControllerUpdateSyncRuleTests.
        var apiKeyId = Guid.NewGuid();
        var apiKey = new ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(apiKey);

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    [Test]
    public async Task GetExampleDataSetAsync_Exists_ReturnsOkWithEntityAsync()
    {
        var dataSet = new ExampleDataSet { Id = 5, Name = "UK Cities", Culture = "en-GB" };
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(5)).ReturnsAsync(dataSet);

        var result = await _controller.GetExampleDataSetAsync(5);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var returned = (ExampleDataSet)((OkObjectResult)result).Value!;
        Assert.That(returned.Id, Is.EqualTo(5));
    }

    [Test]
    public async Task GetExampleDataSetAsync_DoesNotExist_ReturnsNotFoundAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(999)).ReturnsAsync((ExampleDataSet?)null);

        var result = await _controller.GetExampleDataSetAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CreateExampleDataSetAsync_ValidRequest_ReturnsCreatedAsync()
    {
        var request = new CreateExampleDataSetRequest
        {
            Name = "UK Cities",
            Culture = "en-GB",
            Values = new List<string> { "London", "Manchester" }
        };

        ExampleDataSet? created = null;
        _mockExampleDataRepo.Setup(r => r.CreateExampleDataSetAsync(It.IsAny<ExampleDataSet>()))
            .Callback<ExampleDataSet>(ds => { ds.Id = 7; created = ds; })
            .Returns(Task.CompletedTask);
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(7)).ReturnsAsync(() => created);

        var result = await _controller.CreateExampleDataSetAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var dto = (ExampleDataSet)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.Name, Is.EqualTo("UK Cities"));
        Assert.That(dto.Values, Has.Count.EqualTo(2));
        _mockExampleDataRepo.Verify(r => r.CreateExampleDataSetAsync(It.IsAny<ExampleDataSet>()), Times.Once);
    }

    [Test]
    public async Task UpdateExampleDataSetAsync_DoesNotExist_ReturnsNotFoundAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(999)).ReturnsAsync((ExampleDataSet?)null);

        var result = await _controller.UpdateExampleDataSetAsync(999, new UpdateExampleDataSetRequest { Name = "New Name" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateExampleDataSetAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        var dataSet = new ExampleDataSet { Id = 1, Name = "Builtin Set", Culture = "en-GB", BuiltIn = true };
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(1)).ReturnsAsync(dataSet);

        var result = await _controller.UpdateExampleDataSetAsync(1, new UpdateExampleDataSetRequest { Name = "New Name" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockExampleDataRepo.Verify(r => r.UpdateExampleDataSetAsync(It.IsAny<ExampleDataSet>()), Times.Never);
    }

    [Test]
    public async Task UpdateExampleDataSetAsync_ValidRequest_UpdatesAndReturnsOkAsync()
    {
        var dataSet = new ExampleDataSet { Id = 3, Name = "Old Name", Culture = "en-GB" };
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(3)).ReturnsAsync(dataSet);

        var result = await _controller.UpdateExampleDataSetAsync(3, new UpdateExampleDataSetRequest { Name = "New Name" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(dataSet.Name, Is.EqualTo("New Name"));
        _mockExampleDataRepo.Verify(r => r.UpdateExampleDataSetAsync(dataSet), Times.Once);
    }

    [Test]
    public async Task DeleteExampleDataSetAsync_DoesNotExist_ReturnsNotFoundAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(999)).ReturnsAsync((ExampleDataSet?)null);

        var result = await _controller.DeleteExampleDataSetAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task DeleteExampleDataSetAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        var dataSet = new ExampleDataSet { Id = 1, Name = "Builtin Set", Culture = "en-GB", BuiltIn = true };
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(1)).ReturnsAsync(dataSet);

        var result = await _controller.DeleteExampleDataSetAsync(1);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockExampleDataRepo.Verify(r => r.DeleteExampleDataSetAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteExampleDataSetAsync_ValidRequest_DeletesAndReturnsNoContentAsync()
    {
        var dataSet = new ExampleDataSet { Id = 4, Name = "Custom Set", Culture = "en-GB" };
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(4)).ReturnsAsync(dataSet);

        var result = await _controller.DeleteExampleDataSetAsync(4);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        _mockExampleDataRepo.Verify(r => r.DeleteExampleDataSetAsync(4), Times.Once);
    }
}
