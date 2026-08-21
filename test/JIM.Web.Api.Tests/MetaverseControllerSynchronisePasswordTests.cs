// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST entry point for a synchronised password change (#1119): the endpoint that turns "this person's
/// password changed" into one queued change per Connected System they have an account in.
/// <para>
/// Deliberately not the same operation as setting a password on chosen accounts, which stays exactly as it is.
/// That one applies a password the administrator chose to whichever accounts they pick, immediately, and
/// preselects nothing because
/// resetting a forgotten password in one system must not silently reset the others (#1172). This one says the
/// person's password has changed and every enabled system should end up holding it, which is a standing
/// arrangement rather than a choice made per account.
/// </para>
/// </summary>
[TestFixture]
public class MetaverseControllerSynchronisePasswordTests
{
    private const int CorporateAdId = 3;
    private const int HrPortalId = 4;
    private const int UserObjectTypeId = 200;

    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private SyncRepository _syncRepo = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;
    private List<Activity> _createdActivities = null!;
    private Guid _metaverseObjectId;

    [SetUp]
    public void SetUp()
    {
        _metaverseObjectId = Guid.NewGuid();
        _createdActivities = [];

        var repository = new Mock<IRepository>();
        var metaverseRepo = new Mock<IMetaverseRepository>();
        var activityRepo = new Mock<IActivityRepository>();
        var apiKeyRepo = new Mock<IApiKeyRepository>();
        var taskingRepo = new Mock<ITaskingRepository>();
        var serviceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();

        activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _createdActivities.Add(a);
            })
            .Returns(Task.CompletedTask);
        activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        taskingRepo.Setup(r => r.HasQueuedPasswordDeliveryTaskAsync(It.IsAny<int?>())).ReturnsAsync(true);

        metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(It.IsAny<Guid>()))
            .ReturnsAsync(() => new MetaverseObject { Id = _metaverseObjectId, CachedDisplayName = "Ada Lovelace" });

        _connectedSystemRepo.Setup(r => r.GetEnabledPasswordSynchronisationTargetsAsync()).ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>())).ReturnsAsync([]);

        repository.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);
        repository.Setup(r => r.Activity).Returns(activityRepo.Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);
        repository.Setup(r => r.Tasking).Returns(taskingRepo.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        repository.Setup(r => r.ServiceSettings).Returns(serviceSettingsRepo.Object);

        _syncRepo = new SyncRepository();
        _application = new JimApplication(repository.Object, syncRepository: _syncRepo);
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application);

        var apiKeyId = Guid.NewGuid();
        apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
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
            HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    /// <summary>
    /// Arranges Connected Systems that take synchronised passwords, and an account for the identity in each.
    /// </summary>
    private void ArrangeTargets(params (int Id, string Name)[] systems)
    {
        _connectedSystemRepo.Setup(r => r.GetEnabledPasswordSynchronisationTargetsAsync())
            .ReturnsAsync(systems.Select(s => new PasswordSynchronisationTarget
            {
                ConnectedSystemId = s.Id,
                ConnectedSystemName = s.Name,
                TargetObjectTypeId = UserObjectTypeId,
                TimeToLive = TimeSpan.FromDays(7)
            }).ToList());

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(_metaverseObjectId))
            .ReturnsAsync(systems.Select(s => new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = s.Id,
                TypeId = UserObjectTypeId
            }).ToList());
    }

    private async Task<IActionResult> SynchroniseAsync(string password = "Correct-Horse-42",
        PasswordExpiryBehaviour? expiryBehaviour = null)
    {
        return await _controller.SynchroniseMetaverseObjectPasswordAsync(_metaverseObjectId,
            new SynchroniseMetaverseObjectPasswordRequest
            {
                Password = password,
                ExpiryBehaviour = expiryBehaviour
            });
    }

    [Test]
    public async Task Synchronise_QueuesOneChangePerEnabledSystemAsync()
    {
        ArrangeTargets((CorporateAdId, "Corporate AD"), (HrPortalId, "HR Portal"));

        var result = await SynchroniseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Has.Count.EqualTo(2));
            Assert.That(_syncRepo.PendingPasswordChanges.Values.Select(c => c.ConnectedSystemId),
                Is.EquivalentTo(new[] { CorporateAdId, HrPortalId }));
        }
    }

    [Test]
    public async Task Synchronise_ReportsEachTargetWithoutThePasswordAsync()
    {
        ArrangeTargets((CorporateAdId, "Corporate AD"));

        var result = (OkObjectResult)await SynchroniseAsync("Correct-Horse-42");
        var body = (SynchroniseMetaverseObjectPasswordResponse)result.Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.Targets, Has.Exactly(1).Items);
            Assert.That(body.Targets[0].ConnectedSystemName, Is.EqualTo("Corporate AD"));
            Assert.That(body.ActivityId, Is.Not.EqualTo(Guid.Empty),
                "The Activity is the durable record; a caller needs its id to follow the outcome.");
            Assert.That(System.Text.Json.JsonSerializer.Serialize(body), Does.Not.Contain("Correct-Horse-42"),
                "Nothing about the response may carry the password.");
        }
    }

    [Test]
    public async Task Synchronise_NoEnabledSystems_SucceedsAndSaysSoAsync()
    {
        // Requirement 14: a change that reached nothing is still recorded, and says so. Failing would be wrong;
        // the caller did nothing incorrect, and there is genuinely nowhere for the password to go.
        var result = await SynchroniseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            var body = (SynchroniseMetaverseObjectPasswordResponse)((OkObjectResult)result).Value!;
            Assert.That(body.Targets, Is.Empty);
            Assert.That(body.QueuedForNoSystems, Is.True);
        }
    }

    [Test]
    public async Task Synchronise_EmptyPassword_IsRefusedAsync()
    {
        ArrangeTargets((CorporateAdId, "Corporate AD"));

        var result = await SynchroniseAsync(password: "  ");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task Synchronise_UnknownIdentity_IsNotFoundAsync()
    {
        var unknown = Guid.NewGuid();
        var metaverseRepo = Mock.Get(_application.Repository!.Metaverse);
        metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(unknown)).ReturnsAsync((MetaverseObject?)null);

        var result = await _controller.SynchroniseMetaverseObjectPasswordAsync(unknown,
            new SynchroniseMetaverseObjectPasswordRequest { Password = "Correct-Horse-42" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task Synchronise_StoresThePasswordEncryptedAsync()
    {
        // The one thing that must never be true of the queue: a readable password sitting in it.
        ArrangeTargets((CorporateAdId, "Corporate AD"));

        await SynchroniseAsync("Correct-Horse-42");

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.EncryptedPassword, Does.Not.Contain("Correct-Horse-42"));
    }

    [Test]
    public async Task Synchronise_DefaultsToNotForcingAChangeAtNextSignInAsync()
    {
        // A password the person chose themselves must not demand they choose another one; that default belongs
        // to setting a password on somebody's behalf, which is the other operation.
        ArrangeTargets((CorporateAdId, "Corporate AD"));

        await SynchroniseAsync();

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));
    }

    [Test]
    public async Task Synchronise_HonoursARequestedExpiryBehaviourAsync()
    {
        ArrangeTargets((CorporateAdId, "Corporate AD"));

        await SynchroniseAsync(expiryBehaviour: PasswordExpiryBehaviour.RequireChangeAtNextSignIn);

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
    }
}
