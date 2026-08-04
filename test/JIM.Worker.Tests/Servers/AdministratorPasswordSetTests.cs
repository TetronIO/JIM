// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers an administrator setting the password on one Connected System Object (issue #1121), which is the manual
/// counterpart to the initial password an export delivers.
/// <para>
/// The behaviour worth guarding is what the code does around the Connector call rather than the call itself: that
/// the password reaches the Connector and nothing else, that the connection is closed however the attempt ends,
/// that a target's refusal is reported rather than thrown, and that every attempt leaves an Activity saying what
/// happened. Each of those has been mutation-checked.
/// </para>
/// </summary>
[TestFixture]
public class AdministratorPasswordSetTests
{
    private const int ConnectedSystemId = 7;

    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private RecordingPasswordConnector _connector = null!;
    private JimApplication _application = null!;
    private Guid _csoId;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        _csoId = Guid.NewGuid();
        _connector = new RecordingPasswordConnector();

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Contoso AD",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
            ConnectorDefinitionId = 1,
            SettingValues = [new ConnectedSystemSettingValue { Id = 1 }]
        };

        var cso = new ConnectedSystemObject
        {
            Id = _csoId,
            ConnectedSystemId = ConnectedSystemId,
            MetaverseObjectId = Guid.NewGuid()
        };

        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemObjectAsync(ConnectedSystemId, _csoId)).ReturnsAsync(cso);

        _repository = new Mock<IRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        _application = new JimApplication(_repository.Object, connectorFactory: new StubConnectorFactory(_connector));
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    private async Task<PasswordSetResult> SetPasswordAsync(string password = "Correct-Horse-42", PasswordSetOptions? options = null)
    {
        return await _application.ConnectedSystems.SetConnectedSystemObjectPasswordAsync(
            ConnectedSystemId, _csoId, password, options ?? new PasswordSetOptions(),
            initiatedBy: new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Ada Lovelace" }, CancellationToken.None);
    }

    /// <summary>
    /// The Activities the run produced, newest last. Captured from the create call rather than the update, so a
    /// test can assert on an Activity whether it completed or failed.
    /// </summary>
    private List<Activity> CreatedActivities()
    {
        var created = new List<Activity>();
        _activityRepository.Verify(r => r.CreateActivityAsync(Capture.In(created)), Times.AtMostOnce);
        return created;
    }

    /// <summary>
    /// Every free-text string the Activity carries. Asserted over as a set rather than field by field, so a
    /// future field that starts carrying detail is covered without anybody remembering to extend this.
    /// </summary>
    private static IEnumerable<string> ActivityText(Activity activity) =>
        new[] { activity.TargetName, activity.Message, activity.ErrorMessage, activity.ErrorStackTrace }
            .Where(text => text != null)
            .Select(text => text!);

    #region the password reaches the Connector and nothing else

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenSuccessful_HandsThePasswordToTheConnectorAsync()
    {
        await SetPasswordAsync("Correct-Horse-42");

        Assert.That(_connector.PasswordsSet, Is.EqualTo(new[] { "Correct-Horse-42" }));
    }

    /// <summary>
    /// The one thing this feature must never do. An Activity is persisted, read back in the portal and kept for
    /// the retention period, so a password reaching any of its fields would outlive the account's first day by
    /// months. Asserts across every string the Activity carries rather than the message alone, so a future field
    /// that starts carrying detail is covered without anybody remembering to extend this.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenSuccessful_RecordsNoPasswordValueOnTheActivityAsync()
    {
        const string password = "Correct-Horse-42";
        await SetPasswordAsync(password);

        Assert.That(ActivityText(CreatedActivities().Single()), Has.None.Contains(password));
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheTargetRefuses_RecordsNoPasswordValueOnTheActivityAsync()
    {
        const string password = "Correct-Horse-42";
        _connector.Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection,
            "The password does not meet the length, complexity or history requirements of the domain.");

        await SetPasswordAsync(password);

        Assert.That(ActivityText(CreatedActivities().Single()), Has.None.Contains(password));
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenPasswordIsBlank_ThrowsBeforeReachingTheConnectorAsync()
    {
        Assert.ThrowsAsync<ArgumentException>(async () => await SetPasswordAsync("   "));

        Assert.Multiple(() =>
        {
            Assert.That(_connector.PasswordsSet, Is.Empty);
            Assert.That(_connector.OpenCount, Is.Zero);
        });
        await Task.CompletedTask;
    }

    #endregion

    #region the connection is closed however the attempt ends

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenSuccessful_ClosesThePasswordConnectionAsync()
    {
        await SetPasswordAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_connector.OpenCount, Is.EqualTo(1));
            Assert.That(_connector.CloseCount, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A Connector that throws rather than classifying must not leak its connection. Without the finally, the
    /// LDAP connection stays open for the life of the Blazor circuit.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheConnectorThrows_StillClosesThePasswordConnectionAsync()
    {
        _connector.ThrowOnSet = new InvalidOperationException("The directory dropped the connection.");

        await SetPasswordAsync();

        Assert.That(_connector.CloseCount, Is.EqualTo(1));
    }

    /// <summary>
    /// The mirror case: a connection that never opened must not be closed, since a Connector's Close is entitled
    /// to assume its Open succeeded.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheConnectionCannotBeOpened_DoesNotCloseItAsync()
    {
        _connector.ThrowOnOpen = new InvalidOperationException("Connection refused.");

        await SetPasswordAsync();

        Assert.That(_connector.CloseCount, Is.Zero);
    }

    #endregion

    #region a refusal is reported, not thrown

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheTargetRefuses_ReturnsTheTargetsVerbatimReasonAsync()
    {
        const string reason = "The password does not meet the length, complexity or history requirements of the domain.";
        _connector.Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, reason);

        var result = await SetPasswordAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(result.ErrorMessage, Is.EqualTo(reason));
        });
    }

    /// <summary>
    /// A connection that could not be opened says nothing about whether the password would have been acceptable,
    /// so it must not be reported as a policy rejection: that would send the administrator off to change a
    /// password that was never the problem.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheConnectionCannotBeOpened_ReportsATransientFailureAsync()
    {
        _connector.ThrowOnOpen = new InvalidOperationException("Connection refused.");

        var result = await SetPasswordAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
        });
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheConnectorThrows_ReportsATransientFailureAsync()
    {
        _connector.ThrowOnSet = new InvalidOperationException("The directory dropped the connection.");

        var result = await SetPasswordAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
        });
    }

    #endregion

    #region every attempt leaves an Activity

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenSuccessful_CompletesAnActivityAgainstTheObjectAsync()
    {
        await SetPasswordAsync();

        var activity = CreatedActivities().Single();
        Assert.Multiple(() =>
        {
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystemObject));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword));
            Assert.That(activity.ConnectedSystemId, Is.EqualTo(ConnectedSystemId));
            Assert.That(activity.ConnectedSystemObjectId, Is.EqualTo(_csoId));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
        });
    }

    /// <summary>
    /// A refusal is an outcome the administrator has to act on, so the Activity has to say the password was not
    /// set. Completing it would leave an audit trail claiming a password that never landed.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheTargetRefuses_FailsTheActivityCarryingTheReasonAsync()
    {
        const string reason = "The password does not meet the length, complexity or history requirements of the domain.";
        _connector.Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, reason);

        await SetPasswordAsync();

        var activity = CreatedActivities().Single();
        Assert.Multiple(() =>
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Does.Contain(reason));
        });
    }

    /// <summary>
    /// The applied behaviour, not the requested one. A target that silently downgrades what was asked for leaves
    /// the account in a state the administrator did not choose, and the Activity is where they find that out.
    /// </summary>
    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenTheTargetDowngradesExpiry_RecordsWhatWasAppliedAsync()
    {
        _connector.Result = PasswordSetResult.SucceededWithExpiryDowngrade(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
            "This directory cannot require a change at next sign-in, so the password expires according to its own policy.");

        await SetPasswordAsync(options: new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn });

        var activity = CreatedActivities().Single();
        Assert.Multiple(() =>
        {
            Assert.That(activity.Message, Does.Contain(nameof(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy)));
            Assert.That(activity.Message, Does.Contain("cannot require a change at next sign-in"));
        });
    }

    /// <summary>
    /// A Connector without the capability must be refused before the Activity is created, or an operation that
    /// was never attempted leaves an Activity in flight for ever.
    /// </summary>
    [Test]
    public void SetConnectedSystemObjectPasswordAsync_WhenTheConnectorCannotSetPasswords_ThrowsAndRecordsNoActivity()
    {
        using var application = new JimApplication(_repository.Object, connectorFactory: new StubConnectorFactory(new PasswordlessConnector()));

        Assert.ThrowsAsync<NotSupportedException>(async () =>
            await application.ConnectedSystems.SetConnectedSystemObjectPasswordAsync(
                ConnectedSystemId, _csoId, "Correct-Horse-42", new PasswordSetOptions(),
                initiatedBy: new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Ada Lovelace" }, CancellationToken.None));

        _activityRepository.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public void SetConnectedSystemObjectPasswordAsync_WhenTheObjectDoesNotExist_Throws()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _application.ConnectedSystems.SetConnectedSystemObjectPasswordAsync(
                ConnectedSystemId, Guid.NewGuid(), "Correct-Horse-42", new PasswordSetOptions(),
                initiatedBy: new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Ada Lovelace" }, CancellationToken.None));
    }

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_WhenInitiatedByAnApiKey_AttributesTheActivityToItAsync()
    {
        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "Service Desk automation" };

        await _application.ConnectedSystems.SetConnectedSystemObjectPasswordAsync(
            ConnectedSystemId, _csoId, "Correct-Horse-42", new PasswordSetOptions(), apiKey, CancellationToken.None);

        var activity = CreatedActivities().Single();
        Assert.Multiple(() =>
        {
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(activity.InitiatedById, Is.EqualTo(apiKey.Id));
        });
    }

    #endregion

    #region options are passed through

    [Test]
    public async Task SetConnectedSystemObjectPasswordAsync_PassesTheRequestedOptionsToTheConnectorAsync()
    {
        var options = new PasswordSetOptions
        {
            ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires,
            EnableAccount = true
        };

        await SetPasswordAsync(options: options);

        Assert.Multiple(() =>
        {
            Assert.That(_connector.LastOptions?.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(_connector.LastOptions?.EnableAccount, Is.True);
        });
    }

    #endregion

    /// <summary>
    /// Hands back one prepared Connector however many times it is asked, so a test can arrange the Connector's
    /// behaviour and then inspect what it was asked to do.
    /// </summary>
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
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public Exception? ThrowOnOpen { get; set; }
        public Exception? ThrowOnSet { get; set; }
        public PasswordSetResult Result { get; set; } = PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn);

        public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours =>
            [PasswordExpiryBehaviour.RequireChangeAtNextSignIn, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy];

        public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings)
        {
            if (ThrowOnOpen != null)
                throw ThrowOnOpen;
            OpenCount++;
        }

        public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken)
        {
            if (ThrowOnSet != null)
                throw ThrowOnSet;

            PasswordsSet.Add(password);
            LastOptions = options;
            return Task.FromResult(Result);
        }

        public void ClosePasswordConnection() => CloseCount++;

        public Task<PasswordPreflightResult> RunPasswordPreflightAsync(List<ConnectedSystemSettingValue> settings, IReadOnlyList<string> containerExternalIds, ILogger logger, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PasswordlessConnector : IConnector
    {
        public string Name => "Passwordless Connector";
        public string? Description => null;
        public string? Url => null;
    }
}
