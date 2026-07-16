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
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
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
/// Controller-level tests for the UnresolvedReferenceHandling mapping in
/// SynchronisationController.UpdateConnectedSystemAsync: a PUT that sets the value updates the persisted entity, and
/// a PUT that omits it leaves the existing value unchanged. Mirrors the mock harness used by
/// SynchronisationControllerUpdateSettingsTests, extended with Activity and Service Settings repositories because
/// (unlike the settings-validation tests) these tests exercise the full UpdateConnectedSystemAsync path rather than
/// short-circuiting at validation. Configuration change tracking is disabled in setup so the tests assert only the
/// mapping behaviour; snapshot capture itself is covered separately by ConfigurationChangeCaptureCoverageTests.
/// </summary>
[TestFixture]
public class SynchronisationControllerUpdateUnresolvedReferenceHandlingTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IServiceSettingsRepository> _mockSettingsRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private static readonly Guid ApiKeyId = Guid.NewGuid();
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
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockSettingsRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(ApiKeyId)).ReturnsAsync(new ApiKey { Id = ApiKeyId, Name = "test-key" });
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        // Configuration change tracking disabled: these tests exercise the request-to-entity mapping only, not the
        // snapshot capture pipeline.
        _mockSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "false"
            });

        // FileConnector validation checks the file exists for import modes
        _tempCsvPath = Path.GetTempFileName();
        File.WriteAllText(_tempCsvPath, "id,displayName\n1,Test User\n");

        // authenticate as an API key, so the controller resolves no Metaverse user but passes the auth check
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.Role, "Administrator"),
            new(ClaimTypes.NameIdentifier, ApiKeyId.ToString())
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
    public async Task UpdateConnectedSystemAsync_UnresolvedReferenceHandlingSet_UpdatesEntityAsync()
    {
        // Arrange
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error;
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1, true)).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1, false)).ReturnsAsync(connectedSystem);

        var request = new UpdateConnectedSystemRequest { UnresolvedReferenceHandling = UnresolvedReferenceHandling.Ignore };

        // Act
        var result = await _controller.UpdateConnectedSystemAsync(1, request);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(connectedSystem.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Ignore));
        var dto = (ConnectedSystemDetailDto)((OkObjectResult)result).Value!;
        Assert.That(dto.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Ignore));
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_UnresolvedReferenceHandlingOmitted_LeavesEntityUnchangedAsync()
    {
        // Arrange
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Warn;
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1, true)).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1, false)).ReturnsAsync(connectedSystem);

        // No UnresolvedReferenceHandling on the request; only Name is being changed.
        var request = new UpdateConnectedSystemRequest { Name = "Renamed System" };

        // Act
        var result = await _controller.UpdateConnectedSystemAsync(1, request);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(connectedSystem.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Warn));
    }

    /// <summary>
    /// Builds a Connected System using the File Connector's own setting definitions, mirroring
    /// SynchronisationControllerUpdateSettingsTests' helper of the same purpose.
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
