// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Covers the decision layer between a provisioned account and the Connector that will give it a password.
/// <para>
/// The interesting behaviour here is not the happy path. It is which failures JIM tries again and which it
/// stops on, because getting that backwards is expensive in both directions: retrying a policy rejection burns
/// attempts to reach the same answer and hides the configuration change that would fix it, while parking a
/// transient network failure leaves an account without a password until somebody notices.
/// </para>
/// </summary>
[TestFixture]
public class InitialPasswordDeliveryServiceTests
{
    private Mock<IConnectorPasswordManagement> _connector = null!;
    private InitialPasswordDeliveryService _service = null!;
    private ConnectedSystemObject _target = null!;
    private TestCredentialProtection _credentialProtection = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new Mock<IConnectorPasswordManagement>();
        _credentialProtection = new TestCredentialProtection();
        _service = new InitialPasswordDeliveryService(new PasswordGeneratorService(), _credentialProtection);
        _target = new ConnectedSystemObject { Id = Guid.NewGuid() };
    }

    private static SyncRuleInitialPassword EnabledConfiguration() =>
        new() { Enabled = true, Source = InitialPasswordSource.Discovered };

    private SyncRuleInitialPassword StaticConfiguration(string? password) =>
        new()
        {
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPasswordEncryptedValue = password == null ? null : _credentialProtection.Protect(password),
            // Deliberately populated, so the tests below prove the static path ignores it rather than merely not
            // reaching it by accident.
            CustomPolicy = new PasswordGenerationPolicy { Length = 12 }
        };

    /// <summary>
    /// Records every password handed to the Connector, and hands back the list so a test can assert on what was
    /// sent rather than on what the service says it sent.
    /// </summary>
    private List<string> CaptureSentPasswords()
    {
        var sent = new List<string>();
        _connector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
                It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .Callback((ConnectedSystemObject _, string password, PasswordSetOptions _, CancellationToken _) => sent.Add(password))
            .ReturnsAsync(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        return sent;
    }

    private void SetPasswordReturns(PasswordSetResult result) =>
        _connector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
                It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    #region what gets attempted at all
    [Test]
    public async Task DeliverAsync_WhenTheConfigurationIsDisabled_DoesNothingAsync()
    {
        var configuration = new SyncRuleInitialPassword { Enabled = false };

        var result = await _service.DeliverAsync(_connector.Object, _target, configuration, null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.NotApplicable));
        _connector.Verify(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
            It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()), Times.Never,
            "a rule that does not ask for an initial password must not cause one to be generated, let alone set");
    }

    [Test]
    public async Task DeliverAsync_WhenThereIsNoConfiguration_DoesNothingAsync()
    {
        var result = await _service.DeliverAsync(_connector.Object, _target, null, null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.NotApplicable));
    }
    #endregion

    #region classification
    [Test]
    public async Task DeliverAsync_WhenTheTargetAcceptsThePassword_ReportsDeliveredAsync()
    {
        SetPasswordReturns(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Delivered));
            Assert.That(result.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        }
    }

    [Test]
    public async Task DeliverAsync_WhenTheTargetRejectsThePasswordOnPolicy_ParksAsync()
    {
        // The case the whole parked state exists for. Another password from the same configuration is refused
        // for the same reason, so retrying reaches the same answer and hides the fix.
        SetPasswordReturns(PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection,
            "the password does not meet the complexity requirements"));

        var result = await _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Parked));
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(result.Message, Does.Contain("complexity"),
                "the target's own reason is the most useful thing an administrator can be shown, and JIM cannot work it out for itself");
        }
    }

    [Test]
    public async Task DeliverAsync_WhenTheDirectoryIsUnreachable_RetriesAsync()
    {
        SetPasswordReturns(PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "the server is unavailable"));

        var result = await _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Retry));
    }

    /// <summary>
    /// A Connector that throws rather than classifying says nothing about whether the password would have been
    /// acceptable, so it is a transient failure to retry rather than an exception for the pass to fall over on
    /// (#1635, through the shared delivery core).
    /// </summary>
    [Test]
    public async Task DeliverAsync_WhenTheConnectorThrows_RetriesWithTheConnectorsWordsAsync()
    {
        _connector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
                It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The directory dropped the connection."));

        var result = await _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Retry));
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
            Assert.That(result.Message, Is.EqualTo("The directory dropped the connection."));
        }
    }

    [Test]
    public void DeliverAsync_WhenCancelled_Propagates()
    {
        // The one exception that must escape: an aborting run must abort, not grind on classifying its own
        // cancellation as a directory fault.
        _connector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
                It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), null, CancellationToken.None));
    }

    [Test]
    public async Task DeliverAsync_WhenTheConnectedSystemIsMisconfigured_RetriesAsync()
    {
        // A configuration fault is an administrator's to fix, but fixing it does not change the generator, so
        // the work stays pending and succeeds on the next run once the connection or rights are corrected.
        SetPasswordReturns(PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault,
            "the account JIM connects as cannot reset passwords here"));

        var result = await _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Retry));
    }

    [Test]
    public async Task DeliverAsync_WhenTheAccountCannotBeFound_RetriesAsync()
    {
        // Immediately after a create, this is usually replication rather than absence: the object was written to
        // one domain controller and read from another. Parking it would strand an account that exists.
        SetPasswordReturns(PasswordSetResult.Failed(PasswordSetFailureReason.TargetObjectNotFound, "no such object"));

        var result = await _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Retry));
    }

    [Test]
    public async Task DeliverAsync_WhenTheTargetCannotSetPasswordsOnThisObject_ParksAsync()
    {
        // Not a policy rejection, but parked for the same reason: no number of retries changes it, and only a
        // person deciding this rule should not be setting passwords will.
        SetPasswordReturns(PasswordSetResult.Failed(PasswordSetFailureReason.UnsupportedOperation,
            "this object type has no password"));

        var result = await _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Parked));
    }

    [Test]
    public async Task DeliverAsync_WhenTheGeneratorConfigurationCannotBeSatisfied_ParksWithoutCallingTheTargetAsync()
    {
        // An impossible configuration is caught before anything is sent. Parking rather than retrying is the
        // same judgement as a policy rejection: only an administrator changing the configuration resolves it.
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy
            {
                Length = 4,
                MinimumUppercase = 3,
                MinimumLowercase = 3,
                MinimumDigits = 3,
                MinimumSymbols = 3
            }
        };

        var result = await _service.DeliverAsync(_connector.Object, _target, configuration, null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Parked));
        _connector.Verify(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
            It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    #endregion

    #region what reaches the Connector
    [Test]
    public async Task DeliverAsync_PassesTheConfiguredExpiryAndEnabledStateToTheConnectorAsync()
    {
        SetPasswordReturns(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.NeverExpires));
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires,
            EnableAccount = false
        };

        PasswordSetOptions? captured = null;
        _connector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
                It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .Callback((ConnectedSystemObject _, string _, PasswordSetOptions options, CancellationToken _) => captured = options)
            .ReturnsAsync(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.NeverExpires));

        await _service.DeliverAsync(_connector.Object, _target, configuration, null, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured!.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(captured!.EnableAccount, Is.False);
        }
    }

    [Test]
    public async Task DeliverAsync_GeneratesADifferentPasswordForEveryAccountAsync()
    {
        // Two accounts provisioned by the same rule in the same run must not share a password. Generating once
        // and reusing it would be an easy and invisible optimisation to make.
        SetPasswordReturns(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        var passwords = new List<string>();
        _connector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
                It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .Callback((ConnectedSystemObject _, string password, PasswordSetOptions _, CancellationToken _) => passwords.Add(password))
            .ReturnsAsync(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        for (var i = 0; i < 20; i++)
            await _service.DeliverAsync(_connector.Object, new ConnectedSystemObject { Id = Guid.NewGuid() },
                EnabledConfiguration(), null, CancellationToken.None);

        Assert.That(passwords.Distinct(), Has.Exactly(20).Items);
    }

    [Test]
    public async Task DeliverAsync_WhenTheSourceIsDiscovered_GeneratesAgainstTheTargetsPolicyAsync()
    {
        // The point of the Discovered source: a target demanding more than JIM's default gets a password that
        // satisfies it, without an administrator retyping the rule.
        SetPasswordReturns(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        var discovered = new ConnectedSystemPasswordPolicy { MinimumLength = 24 };

        string? captured = null;
        _connector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
                It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .Callback((ConnectedSystemObject _, string password, PasswordSetOptions _, CancellationToken _) => captured = password)
            .ReturnsAsync(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        await _service.DeliverAsync(_connector.Object, _target, EnabledConfiguration(), discovered, CancellationToken.None);

        Assert.That(captured, Has.Length.GreaterThanOrEqualTo(24));
    }

    [Test]
    public async Task DeliverAsync_WhenTheSourceIsCustom_IgnoresTheTargetsDiscoveredPolicyAsync()
    {
        // The mirror of the test above, and the reason Custom exists: an administrator who has set the rules
        // deliberately does not want JIM changing them underneath because a target published something else.
        SetPasswordReturns(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy { Length = 12 }
        };

        string? captured = null;
        _connector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
                It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .Callback((ConnectedSystemObject _, string password, PasswordSetOptions _, CancellationToken _) => captured = password)
            .ReturnsAsync(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        // A discovered policy the custom settings comfortably satisfy, so this isolates which settings were used
        // rather than whether the target would accept the result.
        await _service.DeliverAsync(_connector.Object, _target, configuration,
            new ConnectedSystemPasswordPolicy { MinimumLength = 8 }, CancellationToken.None);

        Assert.That(captured, Has.Length.EqualTo(12));
    }

    /// <summary>
    /// Custom settings decide what JIM generates; they do not exempt the result from what the target demands.
    /// <para>
    /// An administrator who sets a length the Connected System will refuse gets told so, rather than JIM sending
    /// a password on every account and collecting an identical rejection each time. This is the same judgement
    /// as the unsatisfiable-configuration case: only a person changing the settings resolves it.
    /// </para>
    /// </summary>
    [Test]
    public async Task DeliverAsync_WhenCustomSettingsCannotSatisfyTheTarget_ParksWithoutCallingItAsync()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy { Length = 12 }
        };

        var result = await _service.DeliverAsync(_connector.Object, _target, configuration,
            new ConnectedSystemPasswordPolicy { MinimumLength = 30 }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Parked));
            Assert.That(result.Message, Does.Contain("30"), "the administrator needs to know what the target actually requires");
        }
        _connector.Verify(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
            It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    #endregion

    #region the static password
    [Test]
    public async Task DeliverAsync_WhenTheSourceIsStatic_SetsTheStoredPasswordAsync()
    {
        // The whole point of the option: the administrator knows what this password is, because they chose it.
        var sent = CaptureSentPasswords();

        var result = await _service.DeliverAsync(_connector.Object, _target, StaticConfiguration("Brown-Chicken-Ladder-47"),
            null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Delivered));
            Assert.That(sent, Is.EqualTo(new[] { "Brown-Chicken-Ladder-47" }),
                "the stored password is what has to reach the target; anything else and nobody can sign in with what they were told");
        }
    }

    [Test]
    public async Task DeliverAsync_WhenTheSourceIsStatic_SetsTheSamePasswordOnEveryAccountAsync()
    {
        // The exact inverse of the generated case, and the reason this option is recommended against: every
        // account the rule provisions shares one password until each person changes it.
        var sent = CaptureSentPasswords();
        var configuration = StaticConfiguration("Brown-Chicken-Ladder-47");

        for (var i = 0; i < 20; i++)
            await _service.DeliverAsync(_connector.Object, new ConnectedSystemObject { Id = Guid.NewGuid() },
                configuration, null, CancellationToken.None);

        Assert.That(sent.Distinct(), Has.Exactly(1).Items);
    }

    [Test]
    public async Task DeliverAsync_WhenTheSourceIsStatic_IgnoresTheGeneratorSettingsAsync()
    {
        // Custom settings are kept while the source is Static so that switching between the two is not
        // destructive. They must have no bearing on what is delivered while Static is selected.
        var sent = CaptureSentPasswords();

        await _service.DeliverAsync(_connector.Object, _target, StaticConfiguration("Brown-Chicken-Ladder-47"),
            new ConnectedSystemPasswordPolicy { MinimumLength = 8 }, CancellationToken.None);

        Assert.That(sent.Single(), Has.Length.EqualTo(23), "a 12-character generated password would mean the generator ran");
    }

    [Test]
    public async Task DeliverAsync_WhenTheSourceIsStaticButNoPasswordIsStored_ParksWithoutCallingTheTargetAsync()
    {
        // Generating something nobody expects would be the worst of both worlds: the account would work, and the
        // administrator would still be waiting to be told a password that does not exist.
        var result = await _service.DeliverAsync(_connector.Object, _target, StaticConfiguration(null), null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Parked));
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
        }
        _connector.Verify(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
            It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeliverAsync_WhenTheStaticPasswordCannotSatisfyTheTarget_ParksWithoutCallingItAsync()
    {
        // The same judgement as an unsatisfiable generator configuration, and it matters more here: one password
        // is going to every account this rule provisions, so a rejection is not one account's problem.
        var result = await _service.DeliverAsync(_connector.Object, _target, StaticConfiguration("short"),
            new ConnectedSystemPasswordPolicy { MinimumLength = 30 }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Parked));
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(result.Message, Does.Contain("30"), "the administrator needs to know what the target actually requires");
        }
        _connector.Verify(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
            It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeliverAsync_WhenTheStaticPasswordCannotBeDecrypted_ParksAsync()
    {
        // An encryption key that has been rotated or lost takes the password with it. Retrying reaches the same
        // answer for ever, and only an administrator setting the password again resolves it.
        var configuration = StaticConfiguration("Brown-Chicken-Ladder-47");
        _credentialProtection.FailToDecrypt = true;

        var result = await _service.DeliverAsync(_connector.Object, _target, configuration, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(InitialPasswordDeliveryOutcome.Parked));
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(result.Message, Does.Not.Contain(configuration.StaticPasswordEncryptedValue!),
                "the stored value must not be repeated into an Activity or a log on its way out");
        }
        _connector.Verify(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(),
            It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeliverAsync_WhenTheStaticPasswordIsParked_TheReasonNeverRepeatsThePasswordAsync()
    {
        // Parked reasons are shown in the portal, written onto the outstanding record and logged. This is the one
        // delivery path holding a password JIM can read back, so it is the one that could leak it.
        const string password = "short";

        var result = await _service.DeliverAsync(_connector.Object, _target, StaticConfiguration(password),
            new ConnectedSystemPasswordPolicy { MinimumLength = 30 }, CancellationToken.None);

        Assert.That(result.Message, Does.Not.Contain(password));
    }
    #endregion

    #region the password never comes back
    [Test]
    public void InitialPasswordDeliveryResult_HasNowhereToCarryThePasswordBack()
    {
        // The export path logs its results and records them on Activities. Returning the generated value here
        // would put it into all of that, for no reason: nothing downstream of delivery needs to know it.
        string[] credentialWords = ["password", "secret", "credential", "token", "passphrase"];

        var offenders = typeof(InitialPasswordDeliveryResult)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string) &&
                        credentialWords.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.That(offenders, Is.Empty, $"These could carry the generated password out of delivery: {string.Join(", ", offenders)}");
    }
    #endregion

}
