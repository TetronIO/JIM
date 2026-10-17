// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HealthControllerTests
{
    #region Get tests

    [Test]
    public void Get_ReturnsOkResult()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = controller.Get();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void Get_ReturnsHealthyStatus()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = controller.Get() as OkObjectResult;
        var value = result?.Value as HealthStatusResponse;

        Assert.That(value, Is.Not.Null);
        Assert.That(value!.Status, Is.EqualTo("healthy"));
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
        mockRepository.Setup(r => r.HasPendingMigrationsAsync()).ReturnsAsync(false);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

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
        mockRepository.Setup(r => r.HasPendingMigrationsAsync()).ReturnsAsync(false);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = await controller.ReadyAsync() as OkObjectResult;
        var value = result?.Value as HealthStatusResponse;

        Assert.That(value, Is.Not.Null);
        Assert.That(value!.Status, Is.EqualTo("ready"));
    }

    [Test]
    public async Task ReadyAsync_WhenInMaintenanceMode_Returns503()
    {
        var mockRepository = new Mock<IRepository>();
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync())
            .ReturnsAsync(new JIM.Models.Core.ServiceSettings { IsServiceInMaintenanceMode = true });
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);
        mockRepository.Setup(r => r.HasPendingMigrationsAsync()).ReturnsAsync(false);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = await controller.ReadyAsync() as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task ReadyAsync_WhenPendingMigrations_Returns503()
    {
        var mockRepository = new Mock<IRepository>();
        mockRepository.Setup(r => r.HasPendingMigrationsAsync()).ReturnsAsync(true);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = await controller.ReadyAsync() as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task ReadyAsync_WhenPendingMigrations_DoesNotCheckServiceSettings()
    {
        var mockRepository = new Mock<IRepository>();
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockRepository.Setup(r => r.HasPendingMigrationsAsync()).ReturnsAsync(true);
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        await controller.ReadyAsync();

        mockServiceSettingsRepo.Verify(s => s.GetServiceSettingsAsync(), Times.Never);
    }

    [Test]
    public async Task ReadyAsync_WhenDatabaseThrowsException_Returns503()
    {
        var mockRepository = new Mock<IRepository>();
        mockRepository.Setup(r => r.HasPendingMigrationsAsync()).ReturnsAsync(false);
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync())
            .ThrowsAsync(new Exception("Connection failed"));
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = await controller.ReadyAsync() as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task ReadyAsync_WhenDatabaseThrowsException_DoesNotExposeExceptionMessage()
    {
        const string sensitiveMessage = "Failed to connect to 10.0.0.5:5432 as jim_admin on database jim";
        var mockRepository = new Mock<IRepository>();
        mockRepository.Setup(r => r.HasPendingMigrationsAsync()).ReturnsAsync(false);
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync())
            .ThrowsAsync(new Exception(sensitiveMessage));
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = await controller.ReadyAsync() as ObjectResult;

        Assert.That(result?.Value, Is.Not.Null);
        var json = JsonSerializer.Serialize(result!.Value);
        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("status").GetString(), Is.EqualTo("not_ready"));
        Assert.That(document.RootElement.GetProperty("reason").GetString(), Is.EqualTo("error"));
        Assert.That(document.RootElement.TryGetProperty("error", out _), Is.False);
        Assert.That(json, Does.Not.Contain(sensitiveMessage));
    }

    [Test]
    public async Task ReadyAsync_WhenDatabaseThrowsException_LogsExceptionAtErrorLevel()
    {
        var exception = new Exception("Connection failed");
        var mockRepository = new Mock<IRepository>();
        mockRepository.Setup(r => r.HasPendingMigrationsAsync()).ReturnsAsync(false);
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync()).ThrowsAsync(exception);
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);
        var mockLogger = new Mock<ILogger<HealthController>>();

        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(mockLogger.Object, application);

        await controller.ReadyAsync();

        mockLogger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), exception,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    #endregion

    #region Live tests

    [Test]
    public void Live_ReturnsOkResult()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = controller.Live();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void Live_ReturnsAliveStatus()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = controller.Live() as OkObjectResult;
        var value = result?.Value as HealthStatusResponse;

        Assert.That(value, Is.Not.Null);
        Assert.That(value!.Status, Is.EqualTo("alive"));
    }

    #endregion

    #region Version tests

    [Test]
    public void Version_ReturnsOkResult()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = controller.Version();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void Version_ReturnsVersionString()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = controller.Version() as OkObjectResult;
        var value = result?.Value as VersionResponse;

        Assert.That(value, Is.Not.Null);
        var version = value!.Version;
        Assert.That(version, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Version_ReturnsProductName()
    {
        var mockRepository = new Mock<IRepository>();
        var application = new JimApplication(mockRepository.Object);
        var controller = new HealthController(NullLogger<HealthController>.Instance, application);

        var result = controller.Version() as OkObjectResult;
        var value = result?.Value as VersionResponse;

        Assert.That(value, Is.Not.Null);
        Assert.That(value!.Product, Is.EqualTo("JIM"));
    }

    #endregion

    #region Wire format

    // The 200 responses were anonymous objects until they were given types, so that the OpenAPI document
    // describes them. The JSON a client reads must be what it was.

    [Test]
    public void Get_SerialisesAsStatusAndTimestamp()
    {
        var controller = new HealthController(NullLogger<HealthController>.Instance, new JimApplication(new Mock<IRepository>().Object));

        var value = (controller.Get() as OkObjectResult)?.Value;

        Assert.That(SerialisedPropertyNames(value), Is.EqualTo(new[] { "status", "timestamp" }));
    }

    [Test]
    public void Live_SerialisesAsStatusAndTimestamp()
    {
        var controller = new HealthController(NullLogger<HealthController>.Instance, new JimApplication(new Mock<IRepository>().Object));

        var value = (controller.Live() as OkObjectResult)?.Value;

        Assert.That(SerialisedPropertyNames(value), Is.EqualTo(new[] { "status", "timestamp" }));
    }

    [Test]
    public void Version_SerialisesAsProductAndVersion()
    {
        var controller = new HealthController(NullLogger<HealthController>.Instance, new JimApplication(new Mock<IRepository>().Object));

        var value = (controller.Version() as OkObjectResult)?.Value;

        Assert.That(SerialisedPropertyNames(value), Is.EqualTo(new[] { "product", "version" }));
    }

    internal static string[] SerialisedPropertyNames(object? value)
    {
        Assert.That(value, Is.Not.Null);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApiJsonConfiguration.Configure(options);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, value!.GetType(), options));
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    #endregion
}
