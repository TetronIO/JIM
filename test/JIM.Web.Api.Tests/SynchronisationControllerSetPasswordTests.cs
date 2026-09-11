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
using JIM.Models.Staging;
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
/// The account-scoped REST surface for setting a password (#1121, #1635): the same operation as setting it on the
/// person with one account named, for callers that hold the Connected System Object rather than the Metaverse
/// Object.
/// <para>
/// Two things are load-bearing here and are the reason the fixture exists: the response must never carry the
/// password back, and the outcome has to be reported as a state on the target rather than as a status code,
/// because the write happens in the Password Delivery Service after this request has been answered. A refusal by
/// the directory is a Parked target with the directory's own words, not a 400; a 400 is reserved for a request
/// JIM could not act on at all.
/// </para>
/// </summary>
[TestFixture]
public class SynchronisationControllerSetPasswordTests
{
    private const int ConnectedSystemId = 3;
    private const string Password = "Correct-Horse-42";

    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IRepository> _repository = null!;
    private SyncRepository _syncRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private List<Activity> _createdActivities = null!;
    private Guid _csoId;
    private Guid _metaverseObjectId;

    [SetUp]
    public void SetUp()
    {
        _csoId = Guid.NewGuid();
        _metaverseObjectId = Guid.NewGuid();
        _createdActivities = [];

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Contoso AD",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
            ConnectorDefinitionId = 1
        };
        var cso = new ConnectedSystemObject { Id = _csoId, ConnectedSystemId = ConnectedSystemId, MetaverseObjectId = _metaverseObjectId };

        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectAsync(ConnectedSystemId, _csoId)).ReturnsAsync(cso);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(_metaverseObjectId)).ReturnsAsync([cso]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemForPasswordDeliveryAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetPasswordSynchronisationTargetsAsync()).ReturnsAsync([]);

        var metaverseRepo = new Mock<IMetaverseRepository>();
        metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(_metaverseObjectId))
            .ReturnsAsync(() => new MetaverseObject { Id = _metaverseObjectId, CachedDisplayName = "Ada Lovelace" });

        _activityRepo = new Mock<IActivityRepository>();
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _createdActivities.Add(a);
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _createdActivities.FirstOrDefault(a => a.Id == id));
        _activityRepo.Setup(r => r.GetPasswordSynchronisationOutcomesAsync(It.IsAny<Guid>())).ReturnsAsync([]);

        var apiKeyId = Guid.NewGuid();
        var apiKeyRepo = new Mock<IApiKeyRepository>();
        apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });

        _repository = new Mock<IRepository>();
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repository.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);
        _repository.Setup(r => r.Tasking).Returns(new Mock<ITaskingRepository>().Object);
        _repository.Setup(r => r.ServiceSettings).Returns(new Mock<IServiceSettingsRepository>().Object);

        _syncRepo = new SyncRepository();
        _application = BuildApplicationWith(new PasswordCapableConnector(), _syncRepo);
        _controller = BuildControllerFor(_application);

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

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    private async Task<IActionResult> SetPasswordAsync(SetConnectedSystemObjectPasswordRequest? request = null, IPasswordChangeOutcomeWaiter? waiter = null) =>
        await _controller.SetConnectedSystemObjectPasswordAsync(ConnectedSystemId, _csoId,
            request ?? new SetConnectedSystemObjectPasswordRequest { Password = Password, Wait = 0 },
            waiter ?? new RecordingWaiter(null));

    private static SetMetaverseObjectPasswordResponse BodyOf(IActionResult result) =>
        (SetMetaverseObjectPasswordResponse)((ObjectResult)result).Value!;

    private PasswordChangeOutcomes OutcomeWhereTheTargetIs(PasswordChangeTargetState state, string? message = null) => new()
    {
        ActivityId = _createdActivities.Count > 0 ? _createdActivities[0].Id : Guid.Empty,
        MetaverseObjectId = _metaverseObjectId,
        Created = DateTime.UtcNow,
        IsSettled = state is not (PasswordChangeTargetState.Queued or PasswordChangeTargetState.Delivering),
        Targets =
        [
            new PasswordChangeTargetOutcome
            {
                ConnectedSystemId = ConnectedSystemId,
                ConnectedSystemName = "Contoso AD",
                State = state,
                Message = message,
                AttemptCount = state is PasswordChangeTargetState.Set or PasswordChangeTargetState.Parked ? 1 : 0
            }
        ]
    };

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_QueuesOneExplicitChangeForTheAccountAsync()
    {
        var result = await SetPasswordAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            var queued = _syncRepo.PendingPasswordChanges.Values.Single();
            Assert.That(queued.ConnectedSystemId, Is.EqualTo(ConnectedSystemId));
            Assert.That(queued.ConnectedSystemObjectId, Is.EqualTo(_csoId));
            Assert.That(queued.MetaverseObjectId, Is.EqualTo(_metaverseObjectId));
            Assert.That(queued.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            var body = BodyOf(result);
            Assert.That(body.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            Assert.That(body.Targets.Select(t => t.ConnectedSystemObjectId), Is.EqualTo(new Guid?[] { _csoId }));
        }
    }

    /// <summary>
    /// The response is serialised, logged by intermediaries and stored by clients. Whatever else changes about
    /// this endpoint, the password must not appear in what it returns, and must not sit readable in the queue.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_ReturnsNoPasswordValueAndStoresItEncryptedAsync()
    {
        var result = (OkObjectResult)await SetPasswordAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(System.Text.Json.JsonSerializer.Serialize(result.Value), Does.Not.Contain(Password));
            Assert.That(_syncRepo.PendingPasswordChanges.Values.Single().EncryptedPassword, Does.Not.Contain(Password));
        }
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WaitsTenSecondsByDefaultAndReportsTheOutcomeAsync()
    {
        // Decision D6: a named account waits, so a caller resetting a password is told whether it took.
        var waiter = new RecordingWaiter(() => OutcomeWhereTheTargetIs(PasswordChangeTargetState.Set, "Password set."));

        var result = await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = Password }, waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(waiter.Waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(10) }));
            var body = BodyOf(result);
            Assert.That(body.Settled, Is.True);
            Assert.That(body.Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Set));
            Assert.That(body.Targets[0].Message, Is.EqualTo("Password set."));
        }
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WaitZero_ReturnsOnEnqueueAsync()
    {
        var waiter = new RecordingWaiter(null);

        var result = await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = Password, Wait = 0 }, waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(waiter.Waits, Is.Empty);
            var body = BodyOf(result);
            Assert.That(body.Settled, Is.False);
            Assert.That(body.Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Queued));
        }
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WaitRunsOut_Returns202WithWhatIsKnownAsync()
    {
        var waiter = new RecordingWaiter(() => OutcomeWhereTheTargetIs(PasswordChangeTargetState.Delivering));

        var result = await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = Password, Wait = 3 }, waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<AcceptedResult>());
            Assert.That(waiter.Waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(3) }));
            Assert.That(BodyOf(result).Settled, Is.False);
        }
    }

    /// <summary>
    /// A refusal is the directory's answer, carried on the target in its own words. It is not a malformed request,
    /// so it is not a 400: the change was recorded, attempted, and parked for a person to look at.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheTargetRefuses_ReportsParkedWithTheReasonAsync()
    {
        const string reason = "The password does not meet the length, complexity or history requirements of the domain.";
        var waiter = new RecordingWaiter(() => OutcomeWhereTheTargetIs(PasswordChangeTargetState.Parked, reason));

        var result = await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = Password }, waiter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            var target = BodyOf(result).Targets[0];
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Parked));
            Assert.That(target.Message, Is.EqualTo(reason));
        }
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WithNoExpiryBehaviourGiven_RequiresAChangeAtNextSignInAsync()
    {
        await SetPasswordAsync();

        Assert.That(_syncRepo.PendingPasswordChanges.Values.Single().ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_HonoursTheRequestedExpiryBehaviourAsync()
    {
        await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = Password, ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires, Wait = 0 });

        Assert.That(_syncRepo.PendingPasswordChanges.Values.Single().ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
    }

    /// <summary>
    /// Omitted means "leave the account's enabled state alone". False would ask the Connector to disable an
    /// account nobody asked it to touch.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WithNoEnableAccountGiven_LeavesTheAccountsStateAloneAsync()
    {
        await SetPasswordAsync();

        Assert.That(_syncRepo.PendingPasswordChanges.Values.Single().EnableAccount, Is.Null);
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WithEnableAccount_CarriesItOnTheChangeAsync()
    {
        await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = Password, EnableAccount = true, Wait = 0 });

        Assert.That(_syncRepo.PendingPasswordChanges.Values.Single().EnableAccount, Is.True);
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WithAnEmptyPassword_RefusesBeforeQueueingAnythingAsync()
    {
        var result = await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = "   " });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    [TestCase(-1)]
    [TestCase(31)]
    public async Task SetConnectedSystemObjectPasswordAsync_WaitOutOfRange_RefusesBeforeQueueingAnythingAsync(int wait)
    {
        var result = await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = Password, Wait = wait });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheObjectDoesNotExist_ReturnsNotFoundAsync()
    {
        var result = await _controller.SetConnectedSystemObjectPasswordAsync(ConnectedSystemId, Guid.NewGuid(),
            new SetConnectedSystemObjectPasswordRequest { Password = Password }, new RecordingWaiter(null));

        Assert.That(result, Is.TypeOf<NotFoundObjectResult>());
    }

    /// <summary>
    /// The message is shown to an administrator and returned to automation, so it must read as a sentence about
    /// their request rather than mentioning the name of a parameter on a JIM method they cannot see.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheObjectDoesNotExist_DoesNotLeakAParameterNameAsync()
    {
        var result = (NotFoundObjectResult)await _controller.SetConnectedSystemObjectPasswordAsync(ConnectedSystemId, Guid.NewGuid(),
            new SetConnectedSystemObjectPasswordRequest { Password = Password }, new RecordingWaiter(null));

        Assert.That(((ApiErrorResponse)result.Value!).Message, Does.Not.Contain("Parameter"));
    }

    /// <summary>
    /// A password belongs to a person. An account nobody is joined to has no person whose password this would be
    /// and nowhere to record it; the caller is told to join it rather than left with a bare 404.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheObjectIsNotJoined_ReturnsNotFoundSayingSoAsync()
    {
        var unjoinedId = Guid.NewGuid();
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectAsync(ConnectedSystemId, unjoinedId))
            .ReturnsAsync(new ConnectedSystemObject { Id = unjoinedId, ConnectedSystemId = ConnectedSystemId, MetaverseObjectId = null });

        var result = await _controller.SetConnectedSystemObjectPasswordAsync(ConnectedSystemId, unjoinedId,
            new SetConnectedSystemObjectPasswordRequest { Password = Password }, new RecordingWaiter(null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<NotFoundObjectResult>());
            Assert.That(((ApiErrorResponse)((NotFoundObjectResult)result).Value!).Message, Does.Contain("not joined to a Metaverse Object"));
            Assert.That(_syncRepo.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheConnectorCannotSetPasswords_ReturnsBadRequestAsync()
    {
        using var application = BuildApplicationWith(new PasswordlessConnector(), new SyncRepository());
        var controller = BuildControllerFor(application);
        controller.ControllerContext = _controller.ControllerContext;

        var result = await controller.SetConnectedSystemObjectPasswordAsync(ConnectedSystemId, _csoId,
            new SetConnectedSystemObjectPasswordRequest { Password = Password }, new RecordingWaiter(null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("cannot set passwords"));
        }
    }

    /// <summary>
    /// One Activity shape for both origins (#1635): the person's password history shows a reset beside a
    /// propagated change, so the parent is recorded against the person, not against the account.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_RecordsThePasswordActivityAgainstThePersonAsync()
    {
        await SetPasswordAsync();

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword));
            Assert.That(activity.MetaverseObjectId, Is.EqualTo(_metaverseObjectId));
            Assert.That(activity.TargetName, Is.EqualTo("Ada Lovelace"));
        }
    }

    private JimApplication BuildApplicationWith(IConnector connector, SyncRepository syncRepository) =>
        new(_repository.Object, syncRepository: syncRepository, connectorFactory: new StubConnectorFactory(connector));

    private static SynchronisationController BuildControllerFor(JimApplication application) =>
        new(new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

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
    /// Stands in for a Connector that can set passwords. It is never asked to: the endpoint queues, and delivery
    /// belongs to the Password Delivery Service, which is not part of this fixture.
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
