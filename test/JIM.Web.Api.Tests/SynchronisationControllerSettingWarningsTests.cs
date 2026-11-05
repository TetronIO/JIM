// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
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
/// A settings write the Connector warns about saves, and the warnings come back on the response (#2021, D5), so a
/// script learns what will not work just as the portal's administrator does. An error still refuses the write, and its
/// response carries only the errors.
/// </summary>
[TestFixture]
public class SynchronisationControllerSettingWarningsTests
{
    private const int ServerSettingId = 1;
    private static readonly Guid ApiKeyId = Guid.NewGuid();

    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private StubWarningConnector _connector = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private ConnectedSystem _connectedSystem = null!;

    [SetUp]
    public void SetUp()
    {
        var repository = new Mock<IRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        var activityRepository = new Mock<IActivityRepository>();
        var settingsRepository = new Mock<IServiceSettingsRepository>();
        var apiKeyRepository = new Mock<IApiKeyRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        repository.Setup(r => r.Activity).Returns(activityRepository.Object);
        repository.Setup(r => r.ServiceSettings).Returns(settingsRepository.Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeyRepository.Object);
        apiKeyRepository.Setup(r => r.GetByIdAsync(ApiKeyId)).ReturnsAsync(new ApiKey { Id = ApiKeyId, Name = "test-key" });
        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        // Configuration change capture is off: this fixture is about what the save returns, not its snapshot.
        settingsRepository.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "false"
            });

        _connector = new StubWarningConnector();
        _application = new JimApplication(repository.Object, connectorFactory: new StubConnectorFactory(_connector));
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("auth_method", "api_key"),
                        new Claim(ClaimTypes.Role, "Administrator"),
                        new Claim(ClaimTypes.NameIdentifier, ApiKeyId.ToString())
                    ], "TestAuth"))
                }
            }
        };

        var setting = new ConnectorDefinitionSetting { Id = ServerSettingId, Name = "Server", Type = ConnectedSystemSettingType.String };
        _connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "PANOPLY",
            ConnectorDefinition = new ConnectorDefinition { Name = StubWarningConnector.ConnectorName, Settings = [setting] },
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "dc1.panoply.local" }]
        };
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(1, It.IsAny<bool>())).ReturnsAsync(_connectedSystem);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    [Test]
    public async Task UpdateConnectedSystemAsync_SettingsTheConnectorWarnsAbout_SavesAndReturnsTheWarningsAsync()
    {
        _connector.Results.Add(ConnectorSettingValueValidationResult.Warning(_connectedSystem.SettingValues[0], "The account cannot run Enable-Mailbox."));
        _connector.Results.Add(ConnectorSettingValueValidationResult.Warning(null, "Exchange is older than JIM supports."));

        var result = await _controller.UpdateConnectedSystemAsync(1, SetServer("ex1.panoply.local"));

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var response = (ConnectedSystemSaveResponse)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.SettingWarnings.Select(w => (w.Setting, w.Message)), Is.EqualTo(new (string?, string)[]
            {
                ("Server", "The account cannot run Enable-Mailbox."),
                (null, "Exchange is older than JIM supports.")
            }));
            Assert.That(response.Name, Is.EqualTo("PANOPLY"), "the response is the saved Connected System, with its warnings");
        }
        _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemAsync(_connectedSystem), Times.Once);
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_ErrorAndWarning_RefusesWithOnlyTheErrorAsync()
    {
        _connector.Results.Add(new ConnectorSettingValueValidationResult { SettingValue = _connectedSystem.SettingValues[0], IsValid = false, ErrorMessage = "Exchange refused the sign-in." });
        _connector.Results.Add(ConnectorSettingValueValidationResult.Warning(null, "Exchange is older than JIM supports."));

        var result = await _controller.UpdateConnectedSystemAsync(1, SetServer("ex1.panoply.local"));

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var error = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
        Assert.That(error.ValidationErrors!.SelectMany(e => e.Value), Is.EqualTo(new[] { "Exchange refused the sign-in." }));
        _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()), Times.Never);
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_NoSettingsInTheRequest_ReturnsNoWarningsAndDoesNotAskTheConnectorAsync()
    {
        _connector.Results.Add(ConnectorSettingValueValidationResult.Warning(null, "Never asked."));

        var result = await _controller.UpdateConnectedSystemAsync(1, new UpdateConnectedSystemRequest { Name = "PANOPLY AD" });

        var response = (ConnectedSystemSaveResponse)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.SettingWarnings, Is.Empty, "always present, so a script can read it without a null check");
            Assert.That(_connector.TimesAsked, Is.Zero);
        }
    }

    private static UpdateConnectedSystemRequest SetServer(string value) => new()
    {
        SettingValues = new Dictionary<int, ConnectedSystemSettingValueUpdate>
        {
            { ServerSettingId, new ConnectedSystemSettingValueUpdate { StringValue = value } }
        }
    };

    private sealed class StubWarningConnector : IConnector, IConnectorSettings
    {
        public const string ConnectorName = "Stub Warning Connector";

        public string Name => ConnectorName;
        public string? Description => null;
        public string? Url => null;

        public List<ConnectorSettingValueValidationResult> Results { get; } = [];
        public int TimesAsked { get; private set; }

        public List<ConnectorSetting> GetSettings() => [];

        public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settings, Serilog.ILogger logger)
        {
            TimesAsked++;
            return Results;
        }
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }
}
