// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The one open, check, set, classify, close sequence every password JIM writes goes through (#1635).
/// <para>
/// Ported from the immediate set-password path's own tests, because the behaviour they guarded is now the
/// core's: the password reaches the Connector and nothing else, the channel is closed however the attempt ends,
/// a refusal is returned rather than thrown, a Connector that throws is a transient failure, and a channel the
/// Connected System forbids is refused before anything is sent. The delivery lane, the initial-password pass and
/// the interim immediate path all inherit these by calling here.
/// </para>
/// </summary>
[TestFixture]
public class PasswordDeliveryCoreTests
{
    private RecordingPasswordConnector _connector = null!;
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObject _target = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new RecordingPasswordConnector();
        _connectedSystem = new ConnectedSystem { Id = 7, Name = "Contoso AD", SettingValues = [new ConnectedSystemSettingValue { Id = 1 }] };
        _target = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 7 };
    }

    private async Task<PasswordSetResult> DeliverOnceAsync(string password = "Correct-Horse-42", PasswordSetOptions? options = null) =>
        await PasswordDeliveryCore.DeliverOnceAsync(_connector, _connectedSystem, _target, password, options ?? new PasswordSetOptions(), CancellationToken.None);

    #region the password reaches the Connector and nothing else

    [Test]
    public async Task DeliverOnceAsync_WhenSuccessful_HandsThePasswordToTheConnectorAsync()
    {
        await DeliverOnceAsync("Correct-Horse-42");

        Assert.That(_connector.PasswordsSet, Is.EqualTo(new[] { "Correct-Horse-42" }));
    }

    [Test]
    public async Task DeliverOnceAsync_PassesTheRequestedOptionsToTheConnectorAsync()
    {
        await DeliverOnceAsync(options: new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires, EnableAccount = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.LastOptions?.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(_connector.LastOptions?.EnableAccount, Is.True);
        }
    }

    #endregion

    #region the channel is closed however the attempt ends

    [Test]
    public async Task DeliverOnceAsync_WhenSuccessful_OpensAndClosesTheChannelOnceAsync()
    {
        await DeliverOnceAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.OpenCount, Is.EqualTo(1));
            Assert.That(_connector.CloseCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A Connector that throws rather than classifying must not leak its connection. Without the close, the
    /// LDAP connection stays open for the life of the calling process.
    /// </summary>
    [Test]
    public async Task DeliverOnceAsync_WhenTheConnectorThrows_StillClosesTheChannelAsync()
    {
        _connector.ThrowOnSet = new InvalidOperationException("The directory dropped the connection.");

        await DeliverOnceAsync();

        Assert.That(_connector.CloseCount, Is.EqualTo(1));
    }

    /// <summary>
    /// The mirror case: a channel that never opened must not be closed, since a Connector's Close is entitled to
    /// assume its Open succeeded.
    /// </summary>
    [Test]
    public async Task DeliverOnceAsync_WhenTheChannelCannotBeOpened_DoesNotCloseItAsync()
    {
        _connector.ThrowOnOpen = new InvalidOperationException("Connection refused.");

        await DeliverOnceAsync();

        Assert.That(_connector.CloseCount, Is.Zero);
    }

    #endregion

    #region a refusal is reported, not thrown

    [Test]
    public async Task DeliverOnceAsync_WhenTheTargetRefuses_ReturnsTheTargetsVerbatimReasonAsync()
    {
        const string reason = "The password does not meet the length, complexity or history requirements of the domain.";
        _connector.Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, reason);

        var result = await DeliverOnceAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(result.ErrorMessage, Is.EqualTo(reason));
        }
    }

    /// <summary>
    /// A channel that could not be opened says nothing about whether the password would have been acceptable, so
    /// it must not be reported as a policy rejection: that would send the administrator off to change a password
    /// that was never the problem.
    /// </summary>
    [Test]
    public async Task DeliverOnceAsync_WhenTheChannelCannotBeOpened_ReportsATransientFailureAsync()
    {
        _connector.ThrowOnOpen = new InvalidOperationException("Connection refused.");

        var result = await DeliverOnceAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
            Assert.That(result.ErrorMessage, Does.Contain("Connection refused."));
            Assert.That(_connector.PasswordsSet, Is.Empty);
        }
    }

    [Test]
    public async Task DeliverOnceAsync_WhenTheConnectorThrows_ReportsATransientFailureWithItsWordsAsync()
    {
        _connector.ThrowOnSet = new InvalidOperationException("The directory dropped the connection.");

        var result = await DeliverOnceAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
            Assert.That(result.ErrorMessage, Is.EqualTo("The directory dropped the connection."));
        }
    }

    [Test]
    public void SetPasswordAsync_WhenCancelled_Propagates()
    {
        // An aborting caller must abort; cancellation is the one exception that is never classified.
        _connector.ThrowOnSet = new OperationCanceledException();

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            PasswordDeliveryCore.SetPasswordAsync(_connector, _target, "Correct-Horse-42", new PasswordSetOptions(), CancellationToken.None));
    }

    #endregion

    #region Require Secure Transport

    [Test]
    public void OpenChannel_SecureTransportRequiredAndChannelIsNot_RefusesWithoutSending()
    {
        _connectedSystem.RequireSecureTransport = true;
        _connector.IsPasswordChannelSecure = false;

        var opening = PasswordDeliveryCore.OpenChannel(_connector, _connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opening.IsOpen, Is.False);
            Assert.That(opening.ChannelNotSecure, Is.True);
            Assert.That(opening.CouldNotOpenChannel, Is.False);
            Assert.That(opening.Failure!.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault),
                "Retrying helps only once an administrator has corrected the configuration.");
            Assert.That(opening.Failure.ErrorMessage, Does.Contain("Require Secure Transport"),
                "The message has to name the setting, or the administrator cannot act on it.");
            Assert.That(_connector.CloseCount, Is.EqualTo(_connector.OpenCount),
                "The channel opened to make the check must not be left hanging open.");
        }
    }

    [Test]
    public void OpenChannel_WhenTheChannelCannotBeOpened_CarriesTheConnectorsWords()
    {
        _connector.ThrowOnOpen = new InvalidOperationException("Connection refused.");

        var opening = PasswordDeliveryCore.OpenChannel(_connector, _connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opening.IsOpen, Is.False);
            Assert.That(opening.CouldNotOpenChannel, Is.True);
            Assert.That(opening.ChannelNotSecure, Is.False);
            Assert.That(opening.OpenErrorMessage, Is.EqualTo("Connection refused."));
            Assert.That(opening.Failure!.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
        }
    }

    [Test]
    public async Task DeliverOnceAsync_SecureTransportRequiredAndChannelIsNot_SendsNothingAsync()
    {
        _connectedSystem.RequireSecureTransport = true;
        _connector.IsPasswordChannelSecure = false;

        var result = await DeliverOnceAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(_connector.PasswordsSet, Is.Empty, "Nothing may reach the target.");
        }
    }

    [Test]
    public async Task DeliverOnceAsync_SecureTransportRequiredAndChannelIs_SendsAsync()
    {
        _connectedSystem.RequireSecureTransport = true;
        _connector.IsPasswordChannelSecure = true;

        var result = await DeliverOnceAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_connector.PasswordsSet, Has.Exactly(1).Items);
        }
    }

    [Test]
    public async Task DeliverOnceAsync_SecureTransportNotRequiredAndChannelIsNot_SendsAsync()
    {
        // The Connector warns; the choice belongs to the administrator who knows the deployment.
        _connectedSystem.RequireSecureTransport = false;
        _connector.IsPasswordChannelSecure = false;

        var result = await DeliverOnceAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_connector.PasswordsSet, Has.Exactly(1).Items);
        }
    }

    #endregion

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

        public bool IsPasswordChannelSecure { get; set; } = true;

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
}
