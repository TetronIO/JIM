// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for previewing a schema refresh (#421): retrieve the Connected System's schema and report what
/// a refresh would change, without persisting anything. A client previews, inspects the result, then commits with
/// the existing import-schema endpoint. What matters is that the preview really is a read (no persistence, no
/// Activity) and that the result carries the drift signals a caller decides on.
/// </summary>
[TestFixture]
public class SynchronisationControllerSchemaRefreshPreviewTests
{
    private const int ConnectedSystemId = 3;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private string _csvPath = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        _csvPath = Path.Join(Path.GetTempPath(), $"jim-schema-rest-preview-{Guid.NewGuid():N}.csv");
        File.WriteAllText(_csvPath, "id,displayName\n1,Test User\n");

        _application = new JimApplication(_repository.Object);
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        _apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });

        var identity = new ClaimsIdentity(
        [
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        ], "ApiKey");

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
        if (File.Exists(_csvPath))
            File.Delete(_csvPath);
    }

    [Test]
    public async Task PreviewSchemaImport_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(99)).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.PreviewConnectedSystemSchemaImportAsync(99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task PreviewSchemaImport_ReportsWhatWouldChangeAndPersistsNothingAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Number },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "department", Type = AttributeDataType.Text }
                ]
            }
        ];
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);

        var result = await _controller.PreviewConnectedSystemSchemaImportAsync(ConnectedSystemId);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var dto = ok!.Value as SchemaRefreshResultDto;
        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.Success, Is.True);
            Assert.That(dto!.RemovedAttributes["user"], Does.Contain("department"));
            Assert.That(dto!.HasRemovalsOrDefinitionChanges, Is.True);
            _connectedSystemRepo.Verify(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>()), Times.Never,
                "A preview must not persist the merged schema.");
            _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never,
                "A preview changes nothing in JIM, so it must not record an Activity.");
        }
    }

    [Test]
    public async Task PreviewSchemaImport_WhenTheConnectorCannotRead_ReturnsBadRequestAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue =
            Path.Join(Path.GetTempPath(), $"jim-missing-{Guid.NewGuid():N}.csv");
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 7, Name = "user", Selected = true, Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Number }] }
        ];
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);

        var result = await _controller.PreviewConnectedSystemSchemaImportAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };
        _application.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue = _csvPath;
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";
        return connectedSystem;
    }
}
