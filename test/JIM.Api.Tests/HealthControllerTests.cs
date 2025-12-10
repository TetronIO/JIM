using JIM.Web.Controllers.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace JIM.Api.Tests;

[TestFixture]
public class HealthControllerTests
{
    #region Get tests

    [Test]
    public void Get_ReturnsOkResult()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = controller.Get();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void Get_ReturnsHealthyStatus()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = controller.Get() as OkObjectResult;
        var value = result?.Value;

        Assert.That(value, Is.Not.Null);
        var statusProperty = value!.GetType().GetProperty("status");
        Assert.That(statusProperty?.GetValue(value), Is.EqualTo("healthy"));
    }

    #endregion

    #region ReadyAsync tests

    [Test]
    public async Task ReadyAsync_WhenApplicationReady_ReturnsOkResult()
    {
        var mockRepository = new Mock<IRepository>();
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync())
            .ReturnsAsync(new JIM.Models.Core.ServiceSettings { IsServiceInMaintenanceMode = false });
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = await controller.ReadyAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task ReadyAsync_WhenApplicationReady_ReturnsReadyStatus()
    {
        var mockRepository = new Mock<IRepository>();
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync())
            .ReturnsAsync(new JIM.Models.Core.ServiceSettings { IsServiceInMaintenanceMode = false });
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = await controller.ReadyAsync() as OkObjectResult;
        var value = result?.Value;

        Assert.That(value, Is.Not.Null);
        var statusProperty = value!.GetType().GetProperty("status");
        Assert.That(statusProperty?.GetValue(value), Is.EqualTo("ready"));
    }

    [Test]
    public async Task ReadyAsync_WhenInMaintenanceMode_Returns503()
    {
        var mockRepository = new Mock<IRepository>();
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync())
            .ReturnsAsync(new JIM.Models.Core.ServiceSettings { IsServiceInMaintenanceMode = true });
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = await controller.ReadyAsync() as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task ReadyAsync_WhenDatabaseThrowsException_Returns503()
    {
        var mockRepository = new Mock<IRepository>();
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync())
            .ThrowsAsync(new Exception("Connection failed"));
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = await controller.ReadyAsync() as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task ReadyAsync_WhenDatabaseThrowsException_ReturnsErrorMessage()
    {
        var mockRepository = new Mock<IRepository>();
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync())
            .ThrowsAsync(new Exception("Connection failed"));
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = await controller.ReadyAsync() as ObjectResult;
        var value = result?.Value;

        Assert.That(value, Is.Not.Null);
        var errorProperty = value!.GetType().GetProperty("error");
        Assert.That(errorProperty?.GetValue(value), Is.EqualTo("Connection failed"));
    }

    #endregion

    #region Live tests

    [Test]
    public void Live_ReturnsOkResult()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = controller.Live();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void Live_ReturnsAliveStatus()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(application);

        var result = controller.Live() as OkObjectResult;
        var value = result?.Value;

        Assert.That(value, Is.Not.Null);
        var statusProperty = value!.GetType().GetProperty("status");
        Assert.That(statusProperty?.GetValue(value), Is.EqualTo("alive"));
    }

    #endregion
}
