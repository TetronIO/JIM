// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for SynchronisationController.UpdateConnectedSystemAsync settings validation: a settings write that breaks a
/// declarative constraint (required, required-group, required-when) must be rejected with HTTP 400 and structured
/// per-setting errors, and must not be persisted. See #828.
/// </summary>
[TestFixture]
public class SynchronisationControllerUpdateSettingsTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private IExpressionEvaluator _expressionEvaluator = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private string _tempCsvPath = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        // FileConnector validation checks the file exists for import modes
        _tempCsvPath = Path.GetTempFileName();
        File.WriteAllText(_tempCsvPath, "id,displayName\n1,Test User\n");

        // authenticate as an API key, so the controller resolves no Metaverse user but passes the auth check
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.Role, "Administrator")
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
        if (File.Exists(_tempCsvPath))
            File.Delete(_tempCsvPath);
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_SettingsWriteViolatesExactlyOneGroup_ReturnsBadRequestAndDoesNotPersistAsync()
    {
        // Arrange: a valid File system (Object Type set, Object Type Column empty), then a write that also sets
        // Object Type Column, breaking the mutually exclusive group.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1, true)).ReturnsAsync(connectedSystem);

        var objectTypeColumnSettingId = connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type Column").Setting.Id;
        var request = new UpdateConnectedSystemRequest
        {
            SettingValues = new Dictionary<int, ConnectedSystemSettingValueUpdate>
            {
                { objectTypeColumnSettingId, new ConnectedSystemSettingValueUpdate { StringValue = "objectClass" } }
            }
        };

        // Act
        var result = await _controller.UpdateConnectedSystemAsync(1, request);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var error = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
        Assert.That(error.Code, Is.EqualTo(ApiErrorCodes.ValidationError));
        Assert.That(error.ValidationErrors, Is.Not.Null.And.Not.Empty);
        Assert.That(error.ValidationErrors!.Values.SelectMany(v => v).Any(m => m.Contains("only one")), Is.True);
        _mockConnectedSystemRepo.Verify(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()), Times.Never);
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_SettingsWriteMissingRequiredValue_ReturnsBadRequestKeyedBySettingNameAsync()
    {
        // Arrange: clear the required Mode setting via the write
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1, true)).ReturnsAsync(connectedSystem);

        var modeSettingId = connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Mode").Setting.Id;
        var request = new UpdateConnectedSystemRequest
        {
            SettingValues = new Dictionary<int, ConnectedSystemSettingValueUpdate>
            {
                { modeSettingId, new ConnectedSystemSettingValueUpdate { StringValue = "" } }
            }
        };

        // Act
        var result = await _controller.UpdateConnectedSystemAsync(1, request);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var error = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
        Assert.That(error.ValidationErrors, Is.Not.Null);
        Assert.That(error.ValidationErrors!.ContainsKey("Mode"), Is.True);
        _mockConnectedSystemRepo.Verify(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()), Times.Never);
    }

    /// <summary>
    /// Builds a Connected System using the File Connector's own setting definitions, assigning each definition setting
    /// a stable Id so the update request can target settings by Id, mirroring how the API addresses settings.
    /// </summary>
    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };
        _application.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        var nextId = 1;
        foreach (var setting in connectorDefinition.Settings)
            setting.Id = nextId++;

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue = _tempCsvPath;
        return connectedSystem;
    }
}
