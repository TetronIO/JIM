// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for a Container's import scope, and specifically for carving one out of a selection (#1255).
/// </summary>
/// <remarks>
/// A Container states one thing about itself: "manage this" and "do not manage this" cannot both be it. The portal
/// keeps the two apart by construction, which leaves this surface as the only way to ask for both, so it is where
/// the invariant has to be enforced. It is refused rather than silently resolved: guessing which half of a
/// contradictory request the caller meant is how a branch ends up imported that an administrator excluded.
/// </remarks>
[TestFixture]
public class SynchronisationControllerUpdateContainerTests
{
    private const int ConnectedSystemId = 3;
    private const int ContainerId = 31;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private ConnectedSystemContainer _container = null!;
    private List<ConnectedSystemContainer> _persisted = null!;

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

        var connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Directory" };
        _container = new ConnectedSystemContainer
        {
            Id = ContainerId,
            Name = "Service Accounts",
            ExternalId = "OU=Service Accounts,OU=Corp,DC=example,DC=com",
            ConnectedSystem = connectedSystem
        };

        _persisted = [];
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemContainerAsync(ContainerId))
            .ReturnsAsync(() => _container);
        _connectedSystemRepo.Setup(r => r.UpdateConnectedSystemContainerAsync(It.IsAny<ConnectedSystemContainer>()))
            .Callback<ConnectedSystemContainer>(_persisted.Add)
            .Returns(Task.CompletedTask);

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
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task UpdateContainer_Excluded_CarvesTheContainerOutAndReportsItBackAsync()
    {
        var result = await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Excluded = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_container.Excluded, Is.True);
            Assert.That(_persisted, Has.Count.EqualTo(1), "the change is persisted through the audited server path");
            Assert.That(((result as OkObjectResult)?.Value as ConnectedSystemContainerDto)?.Excluded, Is.True,
                "a caller has to be able to read back what it just set");
        }
    }

    [Test]
    public async Task UpdateContainer_ExcludedFalse_HandsTheContainerBackAsync()
    {
        _container.Excluded = true;

        var result = await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Excluded = false });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(_container.Excluded, Is.False);
            Assert.That(_container.Selected, Is.False, "clearing an exclusion restores what the ancestors say; it does not select");
        }
    }

    [Test]
    public async Task UpdateContainer_OmittingExcluded_LeavesTheStoredExclusionAloneAsync()
    {
        _container.Excluded = true;

        await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Scope = ConnectedSystemContainerScope.OneLevel });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_container.Excluded, Is.True);
            Assert.That(_container.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
        }
    }

    [Test]
    public async Task UpdateContainer_SelectedAndExcludedTogether_IsRejectedAsync()
    {
        var result = await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Selected = true, Excluded = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_persisted, Is.Empty, "a contradictory request must change nothing");
        }
    }

    [Test]
    public async Task UpdateContainer_ExcludingAContainerThatIsAlreadySelected_IsRejectedAsync()
    {
        // The stored state is half of the contradiction, so a request naming only the other half still produces one.
        _container.Selected = true;

        var result = await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Excluded = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_container.Selected, Is.True, "the stored state is left exactly as it was");
            Assert.That(_container.Excluded, Is.False);
        }
    }

    [Test]
    public async Task UpdateContainer_SelectingAContainerThatIsAlreadyExcluded_IsRejectedAsync()
    {
        _container.Excluded = true;

        var result = await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Selected = true });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateContainer_ReplacingASelectionWithAnExclusionInOneRequest_IsAcceptedAsync()
    {
        // Saying both halves is how a caller moves a Container from one statement to the other, and is the way past
        // the rejection above rather than a loophole in it.
        _container.Selected = true;

        var result = await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Selected = false, Excluded = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(_container.Selected, Is.False);
            Assert.That(_container.Excluded, Is.True);
        }
    }
}
