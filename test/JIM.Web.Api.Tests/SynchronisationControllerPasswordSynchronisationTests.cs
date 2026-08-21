// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
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
/// The REST surface for a Connected System's Password Synchronisation configuration (#1119, requirement 32).
/// <para>
/// The behaviour worth pinning here is what the endpoints refuse. A configuration saved against a Connector with
/// no password channel, or naming an Object Type that holds no accounts, would queue password changes nothing
/// could ever deliver, and would read as configured the whole time.
/// </para>
/// </summary>
[TestFixture]
public class SynchronisationControllerPasswordSynchronisationTests
{
    private const int ConnectedSystemId = 100;
    private const int UserObjectTypeId = 200;
    private const int GroupObjectTypeId = 201;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private SynchronisationController _controller = null!;

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
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        var application = new JimApplication(_mockRepository.Object, syncRepository: new Mock<ISyncRepository>().Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId, Name = "TestApiKey", KeyHash = "test-hash", KeyPrefix = "test",
            IsEnabled = true, Created = DateTime.UtcNow
        });

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) }
        };
    }

    private static ConnectedSystem BuildConnectedSystem(bool supportsPasswordSet = true) => new()
    {
        Id = ConnectedSystemId,
        Name = "Corporate AD",
        ConnectorDefinitionId = 4,
        ConnectorDefinition = new ConnectorDefinition
        {
            Id = 4, Name = "JIM LDAP Connector", SupportsPasswordSet = supportsPasswordSet
        },
        ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = UserObjectTypeId, Name = "user", Selected = true },
            new ConnectedSystemObjectType { Id = GroupObjectTypeId, Name = "group", Selected = false }
        ],
        ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem,
        // The Connected System update path refuses a system with no setting values at all, so the fixture needs
        // one for the save to be reached.
        SettingValues =
        [
            new ConnectedSystemSettingValue
            {
                Id = 1,
                Setting = new ConnectorDefinitionSetting { Id = 1, Name = "Host", Type = ConnectedSystemSettingType.String },
                StringValue = "dc1.example.local"
            }
        ]
    };

    private void ArrangeConnectedSystem(ConnectedSystem connectedSystem)
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordSynchronisationAsync(ConnectedSystemId))
            .ReturnsAsync(() => connectedSystem.PasswordSynchronisation);
    }

    [Test]
    public async Task GetPasswordSynchronisation_WithNothingConfigured_ReportsUnconfiguredWithDefaultsAsync()
    {
        // Not a 404: the system exists, and "Password Synchronisation has not been set up here" is a different
        // answer from "there is no such system". Reporting JIM's defaults saves a caller knowing them.
        ArrangeConnectedSystem(BuildConnectedSystem());

        var result = await _controller.GetConnectedSystemPasswordSynchronisationAsync(ConnectedSystemId);

        var response = ((OkObjectResult)result).Value as ConnectedSystemPasswordSynchronisationResponse;
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.Configured, Is.False);
            Assert.That(response!.Enabled, Is.False);
            Assert.That(response!.ConnectorSupportsPasswordSet, Is.True);
            Assert.That(response!.EffectiveMaxRetries,
                Is.EqualTo(ConnectedSystemPasswordSynchronisation.DefaultMaxRetries));
            Assert.That(response!.EffectiveTimeToLive, Is.EqualTo(TimeSpan.FromDays(7)));
        }
    }

    [Test]
    public async Task GetPasswordSynchronisation_WhenTheConnectorCannotSetPasswords_SaysSoAsync()
    {
        // The client's cue that there is nothing to configure here, so the portal can hide the option rather
        // than offering one that would be refused on save.
        ArrangeConnectedSystem(BuildConnectedSystem(supportsPasswordSet: false));

        var result = await _controller.GetConnectedSystemPasswordSynchronisationAsync(ConnectedSystemId);

        var response = ((OkObjectResult)result).Value as ConnectedSystemPasswordSynchronisationResponse;
        Assert.That(response!.ConnectorSupportsPasswordSet, Is.False);
    }

    [Test]
    public async Task GetPasswordSynchronisation_WithNoSuchConnectedSystem_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetConnectedSystemPasswordSynchronisationAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdatePasswordSynchronisation_OnAnUnconfiguredSystem_CreatesTheConfigurationAsync()
    {
        var connectedSystem = BuildConnectedSystem();
        ArrangeConnectedSystem(connectedSystem);

        var result = await _controller.UpdateConnectedSystemPasswordSynchronisationAsync(ConnectedSystemId,
            new UpdateConnectedSystemPasswordSynchronisationRequest
            {
                Enabled = true,
                TargetObjectTypeId = UserObjectTypeId
            });

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), Describe(result));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.PasswordSynchronisation, Is.Not.Null);
            Assert.That(connectedSystem.PasswordSynchronisation!.Enabled, Is.True);
            Assert.That(connectedSystem.PasswordSynchronisation!.TargetObjectTypeId, Is.EqualTo(UserObjectTypeId));
        }
    }

    private static string Describe(IActionResult result) =>
        result is ObjectResult objectResult ? System.Text.Json.JsonSerializer.Serialize(objectResult.Value) : result.ToString()!;

    [Test]
    public async Task UpdatePasswordSynchronisation_WithAnOmittedField_LeavesTheStoredValueAloneAsync()
    {
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.PasswordSynchronisation = new ConnectedSystemPasswordSynchronisation
        {
            Id = 9, ConnectedSystemId = ConnectedSystemId, Enabled = true,
            TargetObjectTypeId = UserObjectTypeId, MaxRetries = 8
        };
        ArrangeConnectedSystem(connectedSystem);

        await _controller.UpdateConnectedSystemPasswordSynchronisationAsync(ConnectedSystemId,
            new UpdateConnectedSystemPasswordSynchronisationRequest { RequireSecureTransport = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.PasswordSynchronisation!.RequireSecureTransport, Is.True);
            Assert.That(connectedSystem.PasswordSynchronisation!.MaxRetries, Is.EqualTo(8));
            Assert.That(connectedSystem.PasswordSynchronisation!.Enabled, Is.True);
        }
    }

    [Test]
    public async Task UpdatePasswordSynchronisation_WhenTheConnectorCannotSetPasswords_IsRefusedAsync()
    {
        // The portal hides the option, but this endpoint is reachable without it.
        var connectedSystem = BuildConnectedSystem(supportsPasswordSet: false);
        ArrangeConnectedSystem(connectedSystem);

        var result = await _controller.UpdateConnectedSystemPasswordSynchronisationAsync(ConnectedSystemId,
            new UpdateConnectedSystemPasswordSynchronisationRequest
            {
                Enabled = true, TargetObjectTypeId = UserObjectTypeId
            });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdatePasswordSynchronisation_WithAnUnselectedObjectType_IsRefusedAsync()
    {
        var connectedSystem = BuildConnectedSystem();
        ArrangeConnectedSystem(connectedSystem);

        var result = await _controller.UpdateConnectedSystemPasswordSynchronisationAsync(ConnectedSystemId,
            new UpdateConnectedSystemPasswordSynchronisationRequest
            {
                Enabled = true, TargetObjectTypeId = GroupObjectTypeId
            });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdatePasswordSynchronisation_WithNoTargetObjectType_IsRefusedAsync()
    {
        // Creating a configuration without naming where the accounts live would leave fan-out with nothing to
        // aim at, and the configuration would read as complete.
        var connectedSystem = BuildConnectedSystem();
        ArrangeConnectedSystem(connectedSystem);

        var result = await _controller.UpdateConnectedSystemPasswordSynchronisationAsync(ConnectedSystemId,
            new UpdateConnectedSystemPasswordSynchronisationRequest { Enabled = true });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdatePasswordSynchronisation_WithNoSuchConnectedSystem_ReturnsNotFoundAsync()
    {
        var result = await _controller.UpdateConnectedSystemPasswordSynchronisationAsync(999,
            new UpdateConnectedSystemPasswordSynchronisationRequest { Enabled = true });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdatePasswordSynchronisation_DisablingKeepsTheConfigurationAsync()
    {
        // Disabling must not be the same as removing: a disabled system keeps accumulating queued password
        // changes, and enabling it again delivers them. Removing the configuration would discard the queue,
        // which is why no endpoint does that.
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.PasswordSynchronisation = new ConnectedSystemPasswordSynchronisation
        {
            Id = 9, ConnectedSystemId = ConnectedSystemId, Enabled = true, TargetObjectTypeId = UserObjectTypeId
        };
        ArrangeConnectedSystem(connectedSystem);

        var result = await _controller.UpdateConnectedSystemPasswordSynchronisationAsync(ConnectedSystemId,
            new UpdateConnectedSystemPasswordSynchronisationRequest { Enabled = false });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.PasswordSynchronisation, Is.Not.Null);
            Assert.That(connectedSystem.PasswordSynchronisation!.Enabled, Is.False);
            Assert.That(connectedSystem.PasswordSynchronisation!.TargetObjectTypeId, Is.EqualTo(UserObjectTypeId));
        }
    }
}
