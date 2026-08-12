// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
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
/// Advanced Mode over the REST API: reading a Connected System's Container Scope as text, and stating it as text.
/// </summary>
/// <remarks>
/// An administrator who scripts JIM cannot be left clicking a tree control through a few hundred Containers, which
/// is the case Advanced Mode exists for, so the text is a first-class surface rather than a portal affordance. It
/// applies all-or-nothing and reports every problem at once, tied to the line that caused it: a scope applied
/// halfway is objects silently leaving import scope, and one error at a time is how a large text gets abandoned
/// half-corrected.
/// </remarks>
[TestFixture]
public class SynchronisationControllerContainerScopeTextTests
{
    private const int ConnectedSystemId = 1;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private ConnectedSystem _connectedSystem = null!;
    private List<ConnectedSystem> _persisted = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _connectedSystem = BuildConnectedSystem();
        _persisted = [];

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => _connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => _connectedSystem);
        _connectedSystemRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Callback<ConnectedSystem>(_persisted.Add)
            .Returns(Task.CompletedTask);

        _application = new JimApplication(_repository.Object);
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object,
            _application, new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        _apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId, Name = "TestApiKey", KeyHash = "h", KeyPrefix = "t", IsEnabled = true, Created = DateTime.UtcNow
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
    public void TearDown() => _application?.Dispose();

    #region Reading

    [Test]
    public async Task GetContainerScopeText_WithASelectionAndAnExclusion_WritesOneStatementPerContainerAsync()
    {
        Container("Corp").Selected = true;
        Container("Service Accounts").Excluded = true;

        var result = await _controller.GetConnectedSystemContainerScopeTextAsync(ConnectedSystemId);

        var dto = (result as OkObjectResult)?.Value as ConnectedSystemContainerScopeTextDto;
        Assert.That(dto?.Text, Is.EqualTo(
            """
            include OU=Corp,DC=example,DC=com
            exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
            """));
    }

    [Test]
    public async Task GetContainerScopeText_WithNothingSelected_IsEmptyRatherThanAbsentAsync()
    {
        var result = await _controller.GetConnectedSystemContainerScopeTextAsync(ConnectedSystemId);

        var dto = (result as OkObjectResult)?.Value as ConnectedSystemContainerScopeTextDto;
        Assert.That(dto?.Text, Is.Empty);
    }

    [Test]
    public async Task GetContainerScopeText_ForASystemThatDoesNotExist_IsNotFoundAsync()
    {
        var result = await _controller.GetConnectedSystemContainerScopeTextAsync(404);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region Writing

    [Test]
    public async Task UpdateContainerScopeText_WithAnIncludeAndAnExclude_AppliesBothAndPersistsOnceAsync()
    {
        var result = await _controller.UpdateConnectedSystemContainerScopeTextAsync(ConnectedSystemId,
            new UpdateConnectedSystemContainerScopeTextRequest
            {
                Text = """
                       include OU=Corp,DC=example,DC=com
                       exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
                       """
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(Container("Corp").Selected, Is.True);
            Assert.That(Container("Service Accounts").Excluded, Is.True);
            Assert.That(_persisted, Has.Count.EqualTo(1),
                "the whole scope is one configuration change, so it is recorded as one Activity and one snapshot");
        }
    }

    [Test]
    public async Task UpdateContainerScopeText_ReportsBackTheCanonicalTextItAppliedAsync()
    {
        // What comes back is what a subsequent read returns, so a caller can tell at once whether what it wrote
        // survived intact rather than discovering it on the next run.
        var result = await _controller.UpdateConnectedSystemContainerScopeTextAsync(ConnectedSystemId,
            new UpdateConnectedSystemContainerScopeTextRequest { Text = "+ OU=Corp,DC=example,DC=com" });

        var dto = (result as OkObjectResult)?.Value as ConnectedSystemContainerScopeTextDto;
        Assert.That(dto?.Text, Is.EqualTo("include OU=Corp,DC=example,DC=com"));
    }

    [Test]
    public async Task UpdateContainerScopeText_APathNamingNoContainer_IsRefusedAndChangesNothingAsync()
    {
        var result = await _controller.UpdateConnectedSystemContainerScopeTextAsync(ConnectedSystemId,
            new UpdateConnectedSystemContainerScopeTextRequest
            {
                Text = """
                       include OU=Corp,DC=example,DC=com
                       exclude OU=Contractors,OU=Corp,DC=example,DC=com
                       """
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(Container("Corp").Selected, Is.False,
                "a text that cannot be applied in full is not applied at all");
            Assert.That(_persisted, Is.Empty);
        }
    }

    [Test]
    public async Task UpdateContainerScopeText_ReportsEveryProblemAgainstItsLineAsync()
    {
        var result = await _controller.UpdateConnectedSystemContainerScopeTextAsync(ConnectedSystemId,
            new UpdateConnectedSystemContainerScopeTextRequest
            {
                Text = """
                       omit OU=Corp,DC=example,DC=com
                       exclude OU=Contractors,OU=Corp,DC=example,DC=com
                       """
            });

        var error = (result as BadRequestObjectResult)?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("line 1"),
            "an error an administrator cannot locate in the text they wrote is not an error they can fix");
    }

    [Test]
    public async Task UpdateContainerScopeText_EmptyText_ClearsTheSelectionAsync()
    {
        Container("Corp").Selected = true;
        Container("Service Accounts").Excluded = true;

        var result = await _controller.UpdateConnectedSystemContainerScopeTextAsync(ConnectedSystemId,
            new UpdateConnectedSystemContainerScopeTextRequest { Text = string.Empty });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(Container("Corp").Selected, Is.False);
            Assert.That(Container("Service Accounts").Excluded, Is.False);
        }
    }

    [Test]
    public async Task UpdateContainerScopeText_ForASystemThatDoesNotExist_IsNotFoundAsync()
    {
        var result = await _controller.UpdateConnectedSystemContainerScopeTextAsync(404,
            new UpdateConnectedSystemContainerScopeTextRequest { Text = string.Empty });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region Helpers

    private ConnectedSystemContainer Container(string name) =>
        Flatten(_connectedSystem.Partitions![0].Containers!).Single(c => c.Name == name);

    private static IEnumerable<ConnectedSystemContainer> Flatten(IEnumerable<ConnectedSystemContainer> containers) =>
        containers.SelectMany(c => new[] { c }.Concat(Flatten(c.ChildContainers)));

    private static ConnectedSystem BuildConnectedSystem()
    {
        var serviceAccounts = new ConnectedSystemContainer
        {
            Id = 22, Name = "Service Accounts", ExternalId = "OU=Service Accounts,OU=Corp,DC=example,DC=com"
        };
        var corp = new ConnectedSystemContainer { Id = 21, Name = "Corp", ExternalId = "OU=Corp,DC=example,DC=com" };
        corp.AddChildContainer(serviceAccounts);

        var partition = new ConnectedSystemPartition
        {
            Id = 11, Name = "DC=example,DC=com", Selected = true, Containers = [corp]
        };
        corp.Partition = partition;

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Directory",
            Partitions = [partition],
            ConnectorDefinition = new ConnectorDefinition
            {
                Id = 1, Name = "JIM LDAP Connector", SupportsPartitions = true, SupportsPartitionContainers = true
            },
            // Saving a Connected System validates that it is configured, so the fixture carries the one setting
            // that makes it so; without it the save is refused before the scope change is reached.
            SettingValues =
            [
                new ConnectedSystemSettingValue
                {
                    Id = 1,
                    Setting = new ConnectorDefinitionSetting { Id = 1, Name = "Host", Type = ConnectedSystemSettingType.String },
                    StringValue = "directory.example.com"
                }
            ]
        };
    }

    #endregion
}
