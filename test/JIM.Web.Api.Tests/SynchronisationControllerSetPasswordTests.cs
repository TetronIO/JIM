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
/// The REST surface for an administrator setting the password on one Connected System Object (#1121), the
/// automation counterpart of the portal's set-password dialog.
/// <para>
/// Two things are load-bearing here and are the reason the fixture exists: the response must never carry the
/// password back, and a failure has to be classified into a status code that says what the caller should do
/// next. A directory that was unreachable and a directory that rejected the password call for opposite
/// responses, and collapsing them into one would send administrators to change a password that was fine.
/// </para>
/// </summary>
[TestFixture]
public class SynchronisationControllerSetPasswordTests
{
    private const int ConnectedSystemId = 3;
    private const string Password = "Correct-Horse-42";

    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private RecordingPasswordConnector _connector = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private Guid _csoId;

    [SetUp]
    public void SetUp()
    {
        _csoId = Guid.NewGuid();
        _connector = new RecordingPasswordConnector();

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Contoso AD",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
            ConnectorDefinitionId = 1
        };
        var cso = new ConnectedSystemObject { Id = _csoId, ConnectedSystemId = ConnectedSystemId };

        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectAsync(ConnectedSystemId, _csoId)).ReturnsAsync(cso);

        _activityRepo = new Mock<IActivityRepository>();
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

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

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);

        _application = new JimApplication(repository.Object, connectorFactory: new StubConnectorFactory(_connector));
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

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

    private async Task<IActionResult> SetPasswordAsync(SetConnectedSystemObjectPasswordRequest? request = null) =>
        await _controller.SetConnectedSystemObjectPasswordAsync(ConnectedSystemId, _csoId,
            request ?? new SetConnectedSystemObjectPasswordRequest { Password = Password });

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenSuccessful_SendsThePasswordToTheConnectorAsync()
    {
        var result = await SetPasswordAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(_connector.PasswordsSet, Is.EqualTo(new[] { Password }));
        });
    }

    /// <summary>
    /// The response is serialised, logged by intermediaries and stored by clients. Whatever else changes about
    /// this endpoint, the password must not appear in what it returns.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenSuccessful_ReturnsNoPasswordValueAsync()
    {
        var result = (OkObjectResult)await SetPasswordAsync();

        var response = result.Value as SetConnectedSystemObjectPasswordResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(response), Does.Not.Contain(Password));
    }

    /// <summary>
    /// The applied behaviour rather than the requested one, and the caveat alongside it. A directory that
    /// silently downgrades what was asked for leaves the account in a state the caller did not choose, and an
    /// automation that reported the request back would never find out.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheTargetDowngradesExpiry_ReportsWhatWasAppliedAsync()
    {
        const string warning = "This directory cannot require a change at next sign-in, so the password expires according to its own policy.";
        _connector.Result = PasswordSetResult.SucceededWithExpiryDowngrade(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, warning);

        var result = (OkObjectResult)await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest
        {
            Password = Password,
            ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn
        });

        var response = (SetConnectedSystemObjectPasswordResponse)result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(response.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));
            Assert.That(response.ExpiryBehaviourWarning, Is.EqualTo(warning));
        });
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WithNoExpiryBehaviourGiven_RequiresAChangeAtNextSignInAsync()
    {
        await SetPasswordAsync();

        Assert.That(_connector.LastOptions?.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
    }

    /// <summary>
    /// Omitted means "leave the account's enabled state alone". False would ask the Connector to disable an
    /// account nobody asked it to touch.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WithNoEnableAccountGiven_LeavesTheAccountsStateAloneAsync()
    {
        await SetPasswordAsync();

        Assert.That(_connector.LastOptions?.EnableAccount, Is.Null);
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WithAnEmptyPassword_RefusesBeforeReachingTheConnectorAsync()
    {
        var result = await SetPasswordAsync(new SetConnectedSystemObjectPasswordRequest { Password = "   " });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(_connector.PasswordsSet, Is.Empty);
        });
    }

    /// <summary>
    /// A rejected password is the caller's to fix, so it is a 400 carrying the target's own words.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheTargetRefuses_ReturnsBadRequestWithTheReasonAsync()
    {
        const string reason = "The password does not meet the length, complexity or history requirements of the domain.";
        _connector.Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, reason);

        var result = await SetPasswordAsync();

        var badRequest = result as BadRequestObjectResult;
        Assert.That(badRequest, Is.Not.Null);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(badRequest!.Value), Does.Contain(reason));
    }

    /// <summary>
    /// Not a 400. Nothing was established about the password, so answering as though it were rejected would send
    /// the caller off to change something that was never the problem; a 502 says the target is the problem and
    /// the same request is worth repeating.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheTargetIsUnreachable_ReturnsBadGatewayAsync()
    {
        _connector.ThrowOnOpen = new InvalidOperationException("Connection refused.");

        var result = await SetPasswordAsync();

        Assert.That(result, Is.TypeOf<ObjectResult>());
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(StatusCodes.Status502BadGateway));
    }

    /// <summary>
    /// Immediately after a create, an account the directory has not finished replicating is not a rejection and
    /// not a permanent absence; a 404 tells the caller to repeat the request rather than change it.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheAccountIsNotThereYet_ReturnsNotFoundAsync()
    {
        _connector.Result = PasswordSetResult.Failed(PasswordSetFailureReason.TargetObjectNotFound, "No such object.");

        var result = await SetPasswordAsync();

        Assert.That(result, Is.TypeOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheObjectDoesNotExist_ReturnsNotFoundAsync()
    {
        var result = await _controller.SetConnectedSystemObjectPasswordAsync(ConnectedSystemId, Guid.NewGuid(),
            new SetConnectedSystemObjectPasswordRequest { Password = Password });

        Assert.That(result, Is.TypeOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheConnectorCannotSetPasswords_ReturnsBadRequestAsync()
    {
        using var application = BuildApplicationWith(new PasswordlessConnector());
        var controller = BuildControllerFor(application);

        var result = await controller.SetConnectedSystemObjectPasswordAsync(ConnectedSystemId, _csoId,
            new SetConnectedSystemObjectPasswordRequest { Password = Password });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenSuccessful_RecordsAnActivityAgainstTheObjectAsync()
    {
        await SetPasswordAsync();

        var created = new List<Activity>();
        _activityRepo.Verify(r => r.CreateActivityAsync(Capture.In(created)), Times.Once);
        var activity = created.Single();
        Assert.Multiple(() =>
        {
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystemObject));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword));
            Assert.That(activity.ConnectedSystemObjectId, Is.EqualTo(_csoId));
        });
    }

    private JimApplication BuildApplicationWith(IConnector connector)
    {
        var apiKeyRepo = new Mock<IApiKeyRepository>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);
        return new JimApplication(repository.Object, connectorFactory: new StubConnectorFactory(connector));
    }

    private SynchronisationController BuildControllerFor(JimApplication application)
    {
        var controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = _controller.ControllerContext
        };
        return controller;
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }

    private sealed class RecordingPasswordConnector : IConnector, IConnectorPasswordManagement
    {
        public string Name => "Recording Password Connector";
        public string? Description => null;
        public string? Url => null;

        public List<string> PasswordsSet { get; } = [];
        public PasswordSetOptions? LastOptions { get; private set; }
        public Exception? ThrowOnOpen { get; set; }
        public PasswordSetResult Result { get; set; } = PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn);

        public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours =>
            [PasswordExpiryBehaviour.RequireChangeAtNextSignIn, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy];

        public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings)
        {
            if (ThrowOnOpen != null)
                throw ThrowOnOpen;
        }

        public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken)
        {
            PasswordsSet.Add(password);
            LastOptions = options;
            return Task.FromResult(Result);
        }

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
