// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.TestSupport;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A Connector setting whose feature is behind a flag (<see cref="ConnectorSetting.FeatureFlagKey"/>, #2021): while the
/// flag is off the REST API leaves it out of the Connector Definition and refuses a request that sets it, so an In
/// Development feature cannot be reached by script any more than from the portal.
/// </summary>
[TestFixture]
public class SynchronisationControllerFlaggedSettingTests
{
    private const int DefinitionId = 7;
    private const int HostSettingId = 1;
    private const int FlaggedSettingId = 2;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private ConnectorDefinition _definition = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _definition = new ConnectorDefinition
        {
            Id = DefinitionId,
            Name = "Flagged Settings Test Connector",
            Settings =
            [
                new ConnectorDefinitionSetting { Id = HostSettingId, Name = "Host", Type = ConnectedSystemSettingType.String },
                new ConnectorDefinitionSetting
                {
                    Id = FlaggedSettingId, Name = "Manage Exchange recipients", Type = ConnectedSystemSettingType.CheckBox,
                    FeatureFlagKey = FeatureFlagCatalogue.ExchangeRecipients.Key
                }
            ]
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectorDefinitionAsync(DefinitionId, It.IsAny<bool>())).ReturnsAsync(_definition);
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetConnectorDefinitionAsync_FlagOff_LeavesTheFlaggedSettingOutAsync()
    {
        CreateController(flagsOn: false);

        var dto = Definition(await _controller.GetConnectorDefinitionAsync(DefinitionId));

        Assert.That(dto.Settings.Select(s => s.Name), Is.EqualTo(new[] { "Host" }));
    }

    [Test]
    public async Task GetConnectorDefinitionAsync_FlagOn_IncludesTheFlaggedSettingAsync()
    {
        CreateController(flagsOn: true);

        var dto = Definition(await _controller.GetConnectorDefinitionAsync(DefinitionId));

        Assert.That(dto.Settings.Select(s => s.Name), Is.EqualTo(new[] { "Host", "Manage Exchange recipients" }));
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_SettingAFlaggedSettingWhileItsFlagIsOff_ReturnsBadRequestAndDoesNotPersistAsync()
    {
        CreateController(flagsOn: false);
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "PANOPLY",
            ConnectorDefinition = _definition,
            SettingValues = _definition.Settings.Select(s => new ConnectedSystemSettingValue { Setting = s }).ToList()
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1, true)).ReturnsAsync(connectedSystem);

        var result = await _controller.UpdateConnectedSystemAsync(1, new UpdateConnectedSystemRequest
        {
            SettingValues = new Dictionary<int, ConnectedSystemSettingValueUpdate>
            {
                { FlaggedSettingId, new ConnectedSystemSettingValueUpdate { CheckboxValue = true } }
            }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("Manage Exchange recipients"));
            Assert.That(connectedSystem.SettingValues.Single(sv => sv.Setting.Id == FlaggedSettingId).CheckboxValue, Is.False,
                "nothing is applied when any requested setting is refused");
        }
        _mockConnectedSystemRepo.Verify(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()), Times.Never);
    }

    private void CreateController(bool flagsOn)
    {
        _mockRepository.Setup(r => r.ServiceSettings).Returns(flagsOn
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("auth_method", "api_key"), new Claim(ClaimTypes.Role, "Administrator")], "TestAuth"))
                }
            }
        };
    }

    private static ConnectorDefinitionDto Definition(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        return (ConnectorDefinitionDto)((OkObjectResult)result).Value!;
    }
}
