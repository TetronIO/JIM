// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST entry point for setting a person's password (#1119, #1635): one operation, aimed either at the
/// accounts the caller names or at every Connected System configured for Password Synchronisation.
/// <para>
/// The two modes share the queue, the delivery service, the Activity shape and the response; what the endpoint
/// owns is the contract around them. Named accounts default to expiring at next sign-in and to a ten-second wait
/// for outcomes, and may enable the account; no accounts named defaults to each system's own expiry policy, returns
/// on enqueue, and may not enable anything (decisions D5 and D6). Both are pinned here, because a caller scripting
/// a reset and a self-service portal reporting a change are relying on opposite defaults from the same URL.
/// </para>
/// </summary>
[TestFixture]
public class MetaverseControllerSetPasswordTests
{
    private const int CorporateAdId = 3;
    private const int HrPortalId = 4;
    private const int UserObjectTypeId = 200;
    private const string Password = "Correct-Horse-42";

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
        // The per-target states in the response are read back through the change's Activity.
        activityRepo.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _createdActivities.FirstOrDefault(a => a.Id == id));
        activityRepo.Setup(r => r.GetPasswordSynchronisationOutcomesAsync(It.IsAny<Guid>())).ReturnsAsync([]);

        metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(It.IsAny<Guid>()))
            .ReturnsAsync(() => new MetaverseObject { Id = _metaverseObjectId, CachedDisplayName = "Ada Lovelace" });

        _connectedSystemRepo.Setup(r => r.GetPasswordSynchronisationTargetsAsync()).ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>())).ReturnsAsync([]);

        repository.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);
        repository.Setup(r => r.Activity).Returns(activityRepo.Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);
        repository.Setup(r => r.Tasking).Returns(taskingRepo.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        repository.Setup(r => r.ServiceSettings).Returns(serviceSettingsRepo.Object);

        _syncRepo = new SyncRepository();
        // Named accounts are checked against the Connector's real capability, so the factory has to hand back one
        // that can set passwords.
        _application = new JimApplication(repository.Object, syncRepository: _syncRepo, connectorFactory: new StubConnectorFactory(new PasswordCapableConnector()));
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
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    /// <summary>
    /// Arranges Connected Systems configured for Password Synchronisation, and an account for the person in each:
    /// the propagate case's targets.
    /// </summary>
    private void ArrangeConfiguredSystems(params (int Id, string Name)[] systems)
    {
        _connectedSystemRepo.Setup(r => r.GetPasswordSynchronisationTargetsAsync())
            .ReturnsAsync(systems.Select(s => new PasswordSynchronisationTarget
            {
                ConnectedSystemId = s.Id,
                ConnectedSystemName = s.Name,
                TargetObjectTypeId = UserObjectTypeId,
                Enabled = true,
                TimeToLive = TimeSpan.FromDays(7)
            }).ToList());

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(_metaverseObjectId))
            .ReturnsAsync(systems.Select(s => new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = s.Id,
                TypeId = UserObjectTypeId,
                MetaverseObjectId = _metaverseObjectId
            }).ToList());
    }

    /// <summary>
    /// Arranges the person's accounts, one per system given, in systems with no Password Synchronisation
    /// configuration at all: the named-account case needs none. Returns the account ids in the order given.
    /// </summary>
    private List<Guid> ArrangeAccounts(params (int Id, string Name)[] systems)
    {
        var accounts = systems.Select(s => new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = s.Id,
            TypeId = UserObjectTypeId,
            MetaverseObjectId = _metaverseObjectId
        }).ToList();

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(_metaverseObjectId))
            .ReturnsAsync(accounts);

        foreach (var (id, name) in systems)
        {
            _connectedSystemRepo.Setup(r => r.GetConnectedSystemForPasswordDeliveryAsync(id))
                .ReturnsAsync(new ConnectedSystem
                {
                    Id = id,
                    Name = name,
                    ConnectorDefinitionId = 1,
                    ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "Password Capable Connector" }
                });
        }

        return accounts.Select(a => a.Id).ToList();
    }

    private async Task<IActionResult> SetPasswordAsync(
        string password = Password,
        IReadOnlyList<Guid>? connectedSystemObjectIds = null,
        PasswordExpiryBehaviour? expiryBehaviour = null,
        bool? enableAccount = null,
        int? wait = null,
        IPasswordChangeOutcomeWaiter? waiter = null)
    {
        return await _controller.SetMetaverseObjectPasswordAsync(_metaverseObjectId,
            new SetMetaverseObjectPasswordRequest
            {
                Password = password,
                ConnectedSystemObjectIds = connectedSystemObjectIds,
                ExpiryBehaviour = expiryBehaviour,
                EnableAccount = enableAccount,
                Wait = wait
            },
            waiter ?? new RecordingWaiter(null));
    }

    private static SetMetaverseObjectPasswordResponse BodyOf(IActionResult result) =>
        (SetMetaverseObjectPasswordResponse)((ObjectResult)result).Value!;

    /// <summary>
    /// Builds the outcomes a waiter would hand back for the systems arranged, every target in one state.
    /// </summary>
    private PasswordChangeOutcomes OutcomesWhereEveryTargetIs(PasswordChangeTargetState state, params (int Id, string Name)[] systems) => new()
    {
        ActivityId = _createdActivities.Count > 0 ? _createdActivities[0].Id : Guid.Empty,
        MetaverseObjectId = _metaverseObjectId,
        Created = DateTime.UtcNow,
        IsSettled = state is not (PasswordChangeTargetState.Queued or PasswordChangeTargetState.Delivering),
        Targets = systems.Select(s => new PasswordChangeTargetOutcome
        {
            ConnectedSystemId = s.Id,
            ConnectedSystemName = s.Name,
            State = state,
            Message = state switch
            {
                PasswordChangeTargetState.Set => "Password set.",
                PasswordChangeTargetState.Parked => "The password does not meet the length, complexity or history requirements of the domain.",
                _ => null
            },
            AttemptCount = state is PasswordChangeTargetState.Set or PasswordChangeTargetState.Parked ? 1 : 0,
            NextAttemptAt = state == PasswordChangeTargetState.Retrying ? DateTime.UtcNow.AddMinutes(5) : null
        }).ToList()
    };

    // ---------------------------------------------------------------------------------------------------------
    // Propagate: no accounts named
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public async Task SetPassword_NoAccountsNamed_QueuesOneChangePerConfiguredSystemAsPropagatedAsync()
    {
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"), (HrPortalId, "HR Portal"));

        var result = await SetPasswordAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Has.Count.EqualTo(2));
            Assert.That(_syncRepo.PendingPasswordChanges.Values.Select(c => c.ConnectedSystemId),
                Is.EquivalentTo(new[] { CorporateAdId, HrPortalId }));
            Assert.That(_syncRepo.PendingPasswordChanges.Values.Select(c => c.Origin), Is.All.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(BodyOf(result).Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
        }
    }

    [Test]
    public async Task SetPassword_NoAccountsNamed_ReturnsOnEnqueueWithEveryTargetQueuedAsync()
    {
        // Decision D6: the propagate case does not hold the caller. The states are still read once so the shape
        // matches a waited call, and they read Queued because nothing has had the chance to move yet.
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"), (HrPortalId, "HR Portal"));
        var waiter = new RecordingWaiter(null);

        var result = await SetPasswordAsync(waiter: waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            var body = BodyOf(result);
            Assert.That(body.Settled, Is.False);
            Assert.That(body.Targets.Select(t => t.State), Is.All.EqualTo(PasswordChangeTargetState.Queued));
            Assert.That(body.Targets.Select(t => t.AttemptCount), Is.All.Zero);
            Assert.That(waiter.Waits, Is.Empty, "Without accounts named, and without wait, the endpoint must not hold the caller at all.");
        }
    }

    [Test]
    public async Task SetPassword_NoAccountsNamed_DefaultsToEachSystemsOwnExpiryPolicyAsync()
    {
        // A password the person chose themselves must not demand they choose another one.
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"));

        await SetPasswordAsync();

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));
    }

    [Test]
    public async Task SetPassword_NoAccountsNamed_NoConfiguredSystems_SucceedsAndSaysSoAsync()
    {
        // Requirement 14: a change that reached nothing is still recorded, and says so. Failing would be wrong;
        // the caller did nothing incorrect, and there is genuinely nowhere for the password to go.
        var result = await SetPasswordAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            var body = BodyOf(result);
            Assert.That(body.Targets, Is.Empty);
            Assert.That(body.QueuedForNoSystems, Is.True);
            Assert.That(body.Settled, Is.True);
        }
    }

    [Test]
    public async Task SetPassword_NoAccountsNamed_NoConfiguredSystemsWithWait_DoesNotHoldTheCallerAsync()
    {
        var waiter = new RecordingWaiter(null);

        var result = await SetPasswordAsync(wait: 30, waiter: waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(BodyOf(result).Settled, Is.True);
            Assert.That(waiter.Waits, Is.Empty);
        }
    }

    [Test]
    public async Task SetPassword_NoAccountsNamed_WithWaitAndTheChangeSettles_Returns200WithTheOutcomesAsync()
    {
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"));
        var waiter = new RecordingWaiter(() => OutcomesWhereEveryTargetIs(PasswordChangeTargetState.Set, (CorporateAdId, "Corporate AD")));

        var result = await SetPasswordAsync(wait: 10, waiter: waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            var body = BodyOf(result);
            Assert.That(body.Settled, Is.True);
            Assert.That(body.Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Set));
            Assert.That(body.Targets[0].Message, Is.EqualTo("Password set."));
            Assert.That(body.Targets[0].AttemptCount, Is.EqualTo(1));
            Assert.That(body.Targets[0].Enabled, Is.True, "The enqueue facts stay on the target beside its outcome.");
            Assert.That(waiter.Waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(10) }));
        }
    }

    [Test]
    public async Task SetPassword_NoAccountsNamed_WithEnableAccount_IsRefusedBeforeAnythingIsQueuedAsync()
    {
        // A propagated password reaches accounts an administrator may have disabled on purpose, so the core never
        // enables one. Silently dropping the flag would let the caller believe it happened.
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"));

        var result = await SetPasswordAsync(enableAccount: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("connectedSystemObjectIds"));
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Named accounts
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public async Task SetPassword_AccountsNamed_QueuesExactlyThoseAccountsAsExplicitAsync()
    {
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"), (HrPortalId, "HR Portal"));

        var result = await SetPasswordAsync(connectedSystemObjectIds: [accounts[0]], wait: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            var queued = _syncRepo.PendingPasswordChanges.Values.Single();
            Assert.That(queued.ConnectedSystemId, Is.EqualTo(CorporateAdId));
            Assert.That(queued.ConnectedSystemObjectId, Is.EqualTo(accounts[0]));
            Assert.That(queued.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            var body = BodyOf(result);
            Assert.That(body.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            Assert.That(body.Targets.Select(t => t.ConnectedSystemObjectId), Is.EqualTo(new Guid?[] { accounts[0] }));
        }
    }

    [Test]
    public async Task SetPassword_AccountsNamed_NeedsNoPasswordSynchronisationConfigurationAsync()
    {
        // Decision D1: the administrator named the account. Nothing about the system's Password Synchronisation
        // configuration, present or absent, switched on or off, stands between them and it.
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));
        _connectedSystemRepo.Setup(r => r.GetPasswordSynchronisationTargetsAsync()).ReturnsAsync([]);

        var result = await SetPasswordAsync(connectedSystemObjectIds: accounts, wait: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Has.Count.EqualTo(1));
            Assert.That(BodyOf(result).Targets[0].Enabled, Is.False, "Reported so a caller can say the system is paused for propagation; the change is delivered regardless.");
        }
    }

    [Test]
    public async Task SetPassword_AccountsNamed_WaitsTenSecondsByDefaultAndReportsTheOutcomesAsync()
    {
        // Decision D6: a caller resetting a password is told what each account did with it, and the Password
        // Delivery Service answers in about a second, so ten seconds is a wait almost nobody sits through.
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));
        var waiter = new RecordingWaiter(() => OutcomesWhereEveryTargetIs(PasswordChangeTargetState.Set, (CorporateAdId, "Corporate AD")));

        var result = await SetPasswordAsync(connectedSystemObjectIds: accounts, waiter: waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(waiter.Waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(10) }));
            var body = BodyOf(result);
            Assert.That(body.Settled, Is.True);
            Assert.That(body.Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Set));
        }
    }

    [Test]
    public async Task SetPassword_AccountsNamed_WaitZero_ReturnsOnEnqueueAsync()
    {
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));
        var waiter = new RecordingWaiter(null);

        var result = await SetPasswordAsync(connectedSystemObjectIds: accounts, wait: 0, waiter: waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(waiter.Waits, Is.Empty);
            Assert.That(BodyOf(result).Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Queued));
        }
    }

    [Test]
    public async Task SetPassword_AccountsNamed_WaitRunsOut_Returns202WithWhatIsKnownAsync()
    {
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));
        var waiter = new RecordingWaiter(() => OutcomesWhereEveryTargetIs(PasswordChangeTargetState.Delivering, (CorporateAdId, "Corporate AD")));

        var result = await SetPasswordAsync(connectedSystemObjectIds: accounts, waiter: waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<AcceptedResult>());
            var body = BodyOf(result);
            Assert.That(body.Settled, Is.False);
            Assert.That(body.Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Delivering));
            Assert.That(body.ActivityId, Is.Not.EqualTo(Guid.Empty), "202 still names the Activity to follow.");
        }
    }

    [Test]
    public async Task SetPassword_AccountsNamed_TargetRefuses_Returns200ParkedWithTheTargetsWordsAsync()
    {
        // A refusal is an outcome, not an error: the change was recorded, the target answered, and the answer is
        // on the target. Nothing about it is the caller's request being malformed.
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));
        var waiter = new RecordingWaiter(() => OutcomesWhereEveryTargetIs(PasswordChangeTargetState.Parked, (CorporateAdId, "Corporate AD")));

        var result = await SetPasswordAsync(connectedSystemObjectIds: accounts, waiter: waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            var target = BodyOf(result).Targets[0];
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Parked));
            Assert.That(target.Message, Does.Contain("requirements of the domain"));
        }
    }

    [Test]
    public async Task SetPassword_AccountsNamed_DefaultsToRequiringAChangeAtNextSignInAsync()
    {
        // Somebody else chose this password, so the person should replace it.
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));

        await SetPasswordAsync(connectedSystemObjectIds: accounts, wait: 0);

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
    }

    [Test]
    public async Task SetPassword_AccountsNamed_HonoursEnableAccountAsync()
    {
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));

        await SetPasswordAsync(connectedSystemObjectIds: accounts, enableAccount: true, wait: 0);

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.EnableAccount, Is.True);
    }

    [Test]
    public async Task SetPassword_AccountsNamed_WithoutEnableAccount_LeavesTheAccountAloneAsync()
    {
        // Omitted, not false: false would ask the Connector to disable an account nobody asked it to touch.
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));

        await SetPasswordAsync(connectedSystemObjectIds: accounts, wait: 0);

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.EnableAccount, Is.Null);
    }

    [Test]
    public async Task SetPassword_EmptyAccountList_IsRefusedBeforeAnythingIsQueuedAsync()
    {
        // An empty list is not "nowhere": a caller that meant every configured system omits the list.
        ArrangeAccounts((CorporateAdId, "Corporate AD"));

        var result = await SetPasswordAsync(connectedSystemObjectIds: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
            Assert.That(_createdActivities, Is.Empty, "A refused request leaves no Activity behind.");
        }
    }

    [Test]
    public async Task SetPassword_AccountThatIsNotThisPersons_IsRefusedAsync()
    {
        ArrangeAccounts((CorporateAdId, "Corporate AD"));
        var somebodyElsesAccount = Guid.NewGuid();

        var result = await SetPasswordAsync(connectedSystemObjectIds: [somebodyElsesAccount]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            var error = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
            Assert.That(error.Message, Does.Contain(somebodyElsesAccount.ToString()));
            Assert.That(error.Message, Does.Not.Contain("Parameter"), "The message is for the caller, not about JIM's method signature.");
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task SetPassword_TwoAccountsInOneSystem_IsRefusedAsync()
    {
        // The queue holds one change per person per system; two accounts there would coalesce and one would
        // silently never get the password.
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"), (CorporateAdId, "Corporate AD"));

        var result = await SetPasswordAsync(connectedSystemObjectIds: accounts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("one account per Connected System"));
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task SetPassword_AccountWhoseConnectorCannotSetPasswords_IsRefusedAsync()
    {
        using var application = new JimApplication(_application.Repository!, syncRepository: new SyncRepository(),
            connectorFactory: new StubConnectorFactory(new PasswordlessConnector()));
        var controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, application)
        {
            ControllerContext = _controller.ControllerContext
        };
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));

        var result = await controller.SetMetaverseObjectPasswordAsync(_metaverseObjectId,
            new SetMetaverseObjectPasswordRequest { Password = Password, ConnectedSystemObjectIds = accounts },
            new RecordingWaiter(null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("cannot set passwords"));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Both modes
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    public async Task SetPassword_ReportsEachTargetWithoutThePasswordAsync()
    {
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"));

        var result = await SetPasswordAsync();
        var body = BodyOf(result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.Targets, Has.Exactly(1).Items);
            Assert.That(body.Targets[0].ConnectedSystemName, Is.EqualTo("Corporate AD"));
            Assert.That(body.ActivityId, Is.Not.EqualTo(Guid.Empty),
                "The Activity is the durable record; a caller needs its id to follow the outcome.");
            Assert.That(System.Text.Json.JsonSerializer.Serialize(body), Does.Not.Contain(Password),
                "Nothing about the response may carry the password.");
        }
    }

    [Test]
    public async Task SetPassword_StoresThePasswordEncryptedAsync()
    {
        // The one thing that must never be true of the queue: a readable password sitting in it.
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"));

        await SetPasswordAsync();

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.EncryptedPassword, Does.Not.Contain(Password));
    }

    [Test]
    public async Task SetPassword_EmptyPassword_IsRefusedAsync()
    {
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"));

        var result = await SetPasswordAsync(password: "  ");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task SetPassword_UnknownIdentity_IsNotFoundAsync()
    {
        var unknown = Guid.NewGuid();
        var metaverseRepo = Mock.Get(_application.Repository!.Metaverse);
        metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(unknown)).ReturnsAsync((MetaverseObject?)null);

        var result = await _controller.SetMetaverseObjectPasswordAsync(unknown,
            new SetMetaverseObjectPasswordRequest { Password = Password },
            new RecordingWaiter(null));

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [TestCase(-1)]
    [TestCase(31)]
    public async Task SetPassword_WaitOutOfRange_IsRefusedBeforeAnythingIsQueuedAsync(int wait)
    {
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"));

        var result = await SetPasswordAsync(wait: wait);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task SetPassword_HonoursARequestedExpiryBehaviourInEitherModeAsync()
    {
        ArrangeConfiguredSystems((CorporateAdId, "Corporate AD"));

        await SetPasswordAsync(expiryBehaviour: PasswordExpiryBehaviour.NeverExpires);

        var queued = _syncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(queued.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
    }

    [Test]
    public async Task SetPassword_WithARetryingTarget_ReportsTheNextAttemptAndIsSettledAsync()
    {
        // Retrying is settled by the waiter's measure: the next attempt is minutes away and nobody is held for it.
        var accounts = ArrangeAccounts((CorporateAdId, "Corporate AD"));
        var waiter = new RecordingWaiter(() => OutcomesWhereEveryTargetIs(PasswordChangeTargetState.Retrying, (CorporateAdId, "Corporate AD")));

        var result = await SetPasswordAsync(connectedSystemObjectIds: accounts, waiter: waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            var body = BodyOf(result);
            Assert.That(body.Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Retrying));
            Assert.That(body.Targets[0].NextAttemptAt, Is.Not.Null);
        }
    }

    /// <summary>
    /// A waiter that records what it was asked and answers with whatever the test arranged; null means "answer as
    /// the real one would for a change nothing has touched", which the endpoint must not need for the no-wait path.
    /// </summary>
    private sealed class RecordingWaiter(Func<PasswordChangeOutcomes>? answer) : IPasswordChangeOutcomeWaiter
    {
        public List<TimeSpan> Waits { get; } = [];

        public Task<PasswordChangeOutcomes?> WaitForOutcomesAsync(Guid activityId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Waits.Add(timeout);
            if (answer == null)
                throw new InvalidOperationException("The waiter was not expected to be consulted by this test.");
            return Task.FromResult<PasswordChangeOutcomes?>(answer());
        }
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }

    /// <summary>
    /// Stands in for a Connector that can set passwords. Nothing here is ever asked to deliver: the endpoint
    /// queues, and the Password Delivery Service is not part of this fixture.
    /// </summary>
    private sealed class PasswordCapableConnector : IConnector, IConnectorPasswordManagement
    {
        public string Name => "Password Capable Connector";
        public string? Description => null;
        public string? Url => null;

        public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours =>
            [PasswordExpiryBehaviour.RequireChangeAtNextSignIn, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy];

        public bool IsPasswordChannelSecure => true;

        public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings)
        {
        }

        public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The endpoint queues; it must never deliver inline.");

        public void ClosePasswordConnection()
        {
        }

        public Task<PasswordPreflightResult> RunPasswordPreflightAsync(List<ConnectedSystemSettingValue> settings, IReadOnlyList<string> containerExternalIds, Serilog.ILogger logger, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PasswordlessConnector : IConnector
    {
        public string Name => "Passwordless Connector";
        public string? Description => null;
        public string? Url => null;
    }
}
