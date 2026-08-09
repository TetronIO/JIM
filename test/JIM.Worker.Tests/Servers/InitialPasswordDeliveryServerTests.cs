// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Services;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The initial password delivery pass (#1121): what happens to an account's outstanding record once JIM has
/// tried to give it a password.
/// <para>
/// The decision of retry-or-park lives in <c>InitialPasswordDeliveryService</c> and is tested there. What is
/// under test here is the consequence: a delivered password stops being outstanding, a parked one stops being
/// attempted, and neither the password nor anything resembling it is written anywhere.
/// </para>
/// </summary>
[TestFixture]
public class InitialPasswordDeliveryServerTests
{
    private const int ConnectedSystemId = 42;
    private const int SyncRuleId = 7;

    private SyncRepository _syncRepo = null!;
    private InitialPasswordDeliveryServer _server = null!;
    private ConnectedSystem _connectedSystem = null!;

    [SetUp]
    public void Setup()
    {
        _syncRepo = new SyncRepository();
        _server = new InitialPasswordDeliveryServer(_syncRepo, new PasswordGeneratorService(), () => new TestCredentialProtection());

        _connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Yellowstone Directory",
            ConnectorDefinition = new ConnectorDefinition { Name = "LDAP Connector" }
        };
        _syncRepo.SeedConnectedSystem(_connectedSystem);
        _syncRepo.SeedSyncRule(new SyncRule
        {
            Id = SyncRuleId,
            Name = "Provision Users",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = ConnectedSystemId,
            ProvisionToConnectedSystem = true,
            InitialPassword = new SyncRuleInitialPassword
            {
                SyncRuleId = SyncRuleId,
                Enabled = true,
                ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn
            }
        });
    }

    /// <summary>
    /// A delivered password stops being outstanding. The table is a work list, not a history: keeping the row
    /// would leave the account queued for a delivery it has already had, and the next run would reset a
    /// password somebody may already be using.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WhenTheTargetAcceptsThePassword_ClearsTheRecordAsync()
    {
        var cso = await StageOutstandingAsync();
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.AttemptedCount, Is.EqualTo(1));
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_syncRepo.PendingInitialPasswords, Is.Empty);
        });
        connector.As<IConnectorPasswordManagement>().Verify(
            c => c.SetPasswordAsync(cso, It.IsAny<string>(), It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The password handed to the Connector satisfies the configured generator settings, and is different every
    /// time. Generated at the moment of delivery is the whole reason no secret is ever persisted.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_GeneratesADistinctPasswordPerAccountAsync()
    {
        await StageOutstandingAsync();
        await StageOutstandingAsync();

        var passwords = new List<string>();
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn), passwords.Add);

        await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.That(passwords, Has.Count.EqualTo(2));
        Assert.That(passwords[0], Is.Not.EqualTo(passwords[1]));
        Assert.That(passwords, Has.All.Length.EqualTo(new PasswordGenerationPolicy().Length));
    }

    /// <summary>
    /// A password the target refused parks the record, and records the target's own words.
    /// <para>
    /// Parking is not a severity judgement; it is the statement that nothing except an administrator changing
    /// the configuration could produce a different answer. The verbatim reason is what they act on, because why
    /// a directory refuses a password is a property of that directory's policy and not something JIM can work
    /// out.
    /// </para>
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WhenTheTargetRefusesThePassword_ParksTheRecordWithTheReasonAsync()
    {
        await StageOutstandingAsync();
        const string reason = "The password does not meet the complexity requirements of the domain.";
        var connector = MockConnector(PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, reason));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        var stored = _syncRepo.PendingInitialPasswords.Values.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(result.DeliveredCount, Is.Zero);
            Assert.That(stored.Status, Is.EqualTo(PendingInitialPasswordStatus.Parked));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(stored.TargetMessage, Is.EqualTo(reason));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
            Assert.That(stored.LastAttemptedAt, Is.Not.Null);
        });
    }

    /// <summary>
    /// A parked record is not attempted again. Re-asking a target that has already given its final answer
    /// produces the same answer for ever, while crowding out work that could succeed.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WithAParkedRecord_DoesNotAttemptItAgainAsync()
    {
        await StageOutstandingAsync();
        var connector = MockConnector(PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused."));
        await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        var second = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.That(second.AttemptedCount, Is.Zero);
        Assert.That(_syncRepo.PendingInitialPasswords.Values.Single().AttemptCount, Is.EqualTo(1),
            "a parked record must not accumulate attempts it never made");
    }

    /// <summary>
    /// A transient failure stays outstanding and is attempted again on the next pass, because the next run may
    /// find the directory reachable, the right granted, or the object replicated.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WhenTheTargetIsUnreachable_StaysOutstandingAndRetriesAsync()
    {
        await StageOutstandingAsync();
        var connector = MockConnector(PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "The directory was unreachable."));

        var first = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);
        var second = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.RetryingCount, Is.EqualTo(1));
            Assert.That(second.AttemptedCount, Is.EqualTo(1), "a retryable record is attempted again on the next pass");
            Assert.That(_syncRepo.PendingInitialPasswords.Values.Single().AttemptCount, Is.EqualTo(2));
            Assert.That(_syncRepo.PendingInitialPasswords.Values.Single().Status, Is.EqualTo(PendingInitialPasswordStatus.Pending));
        });
    }

    /// <summary>
    /// Switching the initial password off on the Synchronisation Rule drops the work rather than parking it.
    /// There is nothing to deliver and nothing for an administrator to repair.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WhenTheRuleNoLongerAsksForAPassword_DropsTheRecordAsync()
    {
        await StageOutstandingAsync();
        _syncRepo.SyncRules[SyncRuleId].InitialPassword!.Enabled = false;
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.NoLongerApplicableCount, Is.EqualTo(1));
            Assert.That(result.AttemptedCount, Is.Zero);
            Assert.That(_syncRepo.PendingInitialPasswords, Is.Empty);
        });
        connector.As<IConnectorPasswordManagement>().Verify(
            c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(), It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()),
            Times.Never, "a rule that no longer asks for a password must not produce a round trip to the target");
    }

    /// <summary>
    /// A Connector that cannot set passwords leaves the work exactly where it is, rather than discarding or
    /// parking it: the capability can arrive with a Connector upgrade, and the accounts genuinely are owed
    /// passwords in the meantime.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WithAConnectorThatCannotSetPasswords_LeavesTheWorkAloneAsync()
    {
        await StageOutstandingAsync();
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns("File Connector");

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ConnectorCannotSetPasswords, Is.True);
            Assert.That(result.AttemptedCount, Is.Zero);
            Assert.That(_syncRepo.PendingInitialPasswords.Values.Single().AttemptCount, Is.Zero,
                "nothing was attempted, so nothing should be recorded as attempted");
            Assert.That(_syncRepo.PendingInitialPasswords.Values.Single().Status, Is.EqualTo(PendingInitialPasswordStatus.Pending));
        });
    }

    /// <summary>
    /// A password connection that cannot be opened is reported once, not once per account. One connection
    /// problem is one thing to fix, and charging every waiting account an attempt for it would march them
    /// towards a retry limit for something none of them caused.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WhenThePasswordConnectionCannotBeOpened_ReportsItOnceAsync()
    {
        await StageOutstandingAsync();
        await StageOutstandingAsync();
        await StageOutstandingAsync();

        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        connector.As<IConnectorPasswordManagement>()
            .Setup(c => c.OpenPasswordConnection(It.IsAny<IList<ConnectedSystemSettingValue>>()))
            .Throws(new InvalidOperationException("The directory refused the connection."));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.CouldNotOpenPasswordConnection, Is.True);
            Assert.That(result.PasswordConnectionErrorMessage, Is.EqualTo("The directory refused the connection."));
            Assert.That(result.AttemptedCount, Is.Zero);
            Assert.That(_syncRepo.PendingInitialPasswords.Values.Select(p => p.AttemptCount), Has.All.Zero);
        });
    }

    /// <summary>
    /// The password connection is closed even when a delivery throws, so that a Connector fault cannot leave a
    /// connection to a directory open for the rest of the worker's life.
    /// </summary>
    [Test]
    public void DeliverOutstandingAsync_WhenADeliveryThrows_StillClosesThePasswordConnection()
    {
        StageOutstandingAsync().GetAwaiter().GetResult();
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        connector.As<IConnectorPasswordManagement>()
            .Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(), It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The Connector fell over."));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None));

        connector.As<IConnectorPasswordManagement>().Verify(c => c.ClosePasswordConnection(), Times.Once);
    }

    /// <summary>
    /// Nothing outstanding means no connection is opened at all. An export run on a system not using initial
    /// passwords must not pay for a directory bind it has no use for.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WithNothingOutstanding_OpensNoConnectionAsync()
    {
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.That(result.HasSomethingToReport, Is.False);
        connector.As<IConnectorPasswordManagement>().Verify(
            c => c.OpenPasswordConnection(It.IsAny<IList<ConnectedSystemSettingValue>>()), Times.Never);
    }

    /// <summary>
    /// Only the Connected System being exported is touched. A password channel opened for one directory must
    /// never be handed an account that lives in another.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_LeavesAnotherConnectedSystemsWorkAloneAsync()
    {
        await StageOutstandingAsync();
        await StageOutstandingAsync(connectedSystemId: 99);
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.That(result.DeliveredCount, Is.EqualTo(1));
        Assert.That(_syncRepo.PendingInitialPasswords.Values.Single().ConnectedSystemId, Is.EqualTo(99));
    }

    #region Time-to-live expiry

    /// <summary>
    /// An initial password exists to get somebody into an account they have just been given. Weeks later that
    /// purpose has passed, and another automatic attempt is not what the account needs; a person is.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WithARecordPastItsExpiry_ExpiresItInsteadOfAttemptingItAsync()
    {
        await StageOutstandingAsync(expiresAt: DateTime.UtcNow.AddDays(-1));
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        var stored = _syncRepo.PendingInitialPasswords.Values.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.ExpiredCount, Is.EqualTo(1));
            Assert.That(result.AttemptedCount, Is.Zero, "an expired record must not be attempted");
            Assert.That(stored.Status, Is.EqualTo(PendingInitialPasswordStatus.Expired));
        });
    }

    /// <summary>
    /// Recorded, not removed. An account that quietly stopped being owed a password, with nothing left to say
    /// so, is exactly the silent loss the rest of this feature is built to avoid: nobody would ever learn that
    /// it was provisioned without a working password.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WithARecordPastItsExpiry_KeepsTheEvidenceAsync()
    {
        await StageOutstandingAsync(expiresAt: DateTime.UtcNow.AddDays(-1));
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.That(_syncRepo.PendingInitialPasswords.Values.Count(), Is.EqualTo(1),
            "the expired record is the only trace that this account never got its password");
    }

    /// <summary>
    /// Parked records expire too. Parking waits for an administrator, and one who never comes is exactly the
    /// case an expiry is for; leaving it parked for ever would hold a permanent needs-attention marker for work
    /// nobody is going to do.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WithAParkedRecordPastItsExpiry_ExpiresItAsync()
    {
        await StageOutstandingAsync();
        var refusing = MockConnector(PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused."));
        await _server.DeliverOutstandingAsync(_connectedSystem, refusing.Object, CancellationToken.None);
        _syncRepo.PendingInitialPasswords.Values.Single().ExpiresAt = DateTime.UtcNow.AddDays(-1);

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, refusing.Object, CancellationToken.None);

        Assert.That(result.ExpiredCount, Is.EqualTo(1));
        Assert.That(_syncRepo.PendingInitialPasswords.Values.Single().Status,
            Is.EqualTo(PendingInitialPasswordStatus.Expired));
    }

    /// <summary>
    /// A record still within its time to live is ordinary outstanding work and must be attempted as usual.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WithARecordWithinItsExpiry_AttemptsItAsync()
    {
        await StageOutstandingAsync(expiresAt: DateTime.UtcNow.AddDays(1));
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.That(result.ExpiredCount, Is.Zero);
        Assert.That(result.DeliveredCount, Is.EqualTo(1));
    }

    /// <summary>
    /// A record with no expiry never expires. Rows staged before initial passwords carried a time to live have
    /// no value here, and inventing one for them would expire work an administrator is still waiting on.
    /// </summary>
    [Test]
    public async Task DeliverOutstandingAsync_WithARecordThatHasNoExpiry_AttemptsItAsync()
    {
        await StageOutstandingAsync(expiresAt: null);
        var connector = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _server.DeliverOutstandingAsync(_connectedSystem, connector.Object, CancellationToken.None);

        Assert.That(result.ExpiredCount, Is.Zero);
        Assert.That(result.DeliveredCount, Is.EqualTo(1));
    }

    #endregion

    #region Releasing parked work

    /// <summary>
    /// Parking is deliberate, and it is only safe because an administrator changing the configuration can undo
    /// it. Without this the parked account is stuck for ever: nothing else in the system moves a record out of
    /// Parked, and the delivery pass will not look at one.
    /// </summary>
    [Test]
    public async Task ReleaseParkedForSyncRuleAsync_WithAParkedRecord_MakesItOutstandingAgainAsync()
    {
        await StageOutstandingAsync();
        var refusing = MockConnector(PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused."));
        await _server.DeliverOutstandingAsync(_connectedSystem, refusing.Object, CancellationToken.None);

        var released = await _server.ReleaseParkedForSyncRuleAsync(SyncRuleId);

        var accepting = MockConnector(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        var retry = await _server.DeliverOutstandingAsync(_connectedSystem, accepting.Object, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(released, Is.EqualTo(1));
            Assert.That(retry.AttemptedCount, Is.EqualTo(1), "the released record must be attempted again");
            Assert.That(retry.DeliveredCount, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The stale refusal goes with the release. Leaving it would have the Synchronisation Rule report a reason
    /// for a record that is no longer parked, so an administrator who had just fixed the configuration would
    /// still be looking at the complaint that sent them there.
    /// </summary>
    [Test]
    public async Task ReleaseParkedForSyncRuleAsync_WithAParkedRecord_ClearsTheStaleRefusalAsync()
    {
        await StageOutstandingAsync();
        var refusing = MockConnector(PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused."));
        await _server.DeliverOutstandingAsync(_connectedSystem, refusing.Object, CancellationToken.None);

        await _server.ReleaseParkedForSyncRuleAsync(SyncRuleId);

        var stored = _syncRepo.PendingInitialPasswords.Values.Single();
        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(PendingInitialPasswordStatus.Pending));
            Assert.That(stored.FailureReason, Is.Null);
            Assert.That(stored.TargetMessage, Is.Null);
            Assert.That(stored.AttemptCount, Is.EqualTo(1),
                "the release is not a new attempt, so the count of real attempts must survive it");
        });
    }

    /// <summary>
    /// Saving one Synchronisation Rule says nothing about the configuration of any other, so a release must not
    /// reach past its own rule and set another rule's accounts retrying against settings nobody has touched.
    /// </summary>
    [Test]
    public async Task ReleaseParkedForSyncRuleAsync_ForAnotherRule_LeavesItsParkedRecordsAloneAsync()
    {
        await StageOutstandingAsync();
        var refusing = MockConnector(PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused."));
        await _server.DeliverOutstandingAsync(_connectedSystem, refusing.Object, CancellationToken.None);

        var released = await _server.ReleaseParkedForSyncRuleAsync(SyncRuleId + 1);

        Assert.That(released, Is.Zero);
        Assert.That(_syncRepo.PendingInitialPasswords.Values.Single().Status,
            Is.EqualTo(PendingInitialPasswordStatus.Parked));
    }

    /// <summary>
    /// A record that was never parked is already going to be retried, so a release must leave it exactly as it
    /// is rather than resetting anything about it.
    /// </summary>
    [Test]
    public async Task ReleaseParkedForSyncRuleAsync_WithARecordAwaitingRetry_LeavesItUntouchedAsync()
    {
        await StageOutstandingAsync();
        var unreachable = MockConnector(PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Unreachable."));
        await _server.DeliverOutstandingAsync(_connectedSystem, unreachable.Object, CancellationToken.None);

        var released = await _server.ReleaseParkedForSyncRuleAsync(SyncRuleId);

        var stored = _syncRepo.PendingInitialPasswords.Values.Single();
        Assert.Multiple(() =>
        {
            Assert.That(released, Is.Zero, "nothing was parked, so nothing was released");
            Assert.That(stored.Status, Is.EqualTo(PendingInitialPasswordStatus.Pending));
            Assert.That(stored.TargetMessage, Is.EqualTo("Unreachable."),
                "the reason for a record still awaiting retry is current, not stale, and must survive");
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
        });
    }

    #endregion

    #region assessing a configuration before it is saved

    /// <summary>
    /// A rule with no initial-password configuration at all has nothing that could be wrong with it. This is the
    /// state every Synchronisation Rule starts in, so a caller must be able to ask about it without a guard.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithNoConfiguration_FindsNothingToFix()
    {
        Assert.That(_server.AssessConfiguration(null, null), Is.Empty);
    }

    /// <summary>
    /// Settings that are switched off are not settings that will run, so they are nobody's problem to fix. A rule
    /// carrying an unusable configuration it does not use must still be savable, or turning the feature off would
    /// not be a way out of an unsatisfiable one.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithTheFeatureOff_FindsNothingToFix()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = false,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = UnsatisfiablePolicy()
        };

        Assert.That(_server.AssessConfiguration(configuration, null), Is.Empty);
    }

    /// <summary>
    /// A rule set to give every account one password, with no password to give, would park every account it
    /// provisions. That is the same class of fault as an unsatisfiable generator configuration and is reported
    /// the same way, before it is saved rather than one account at a time afterwards.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithAStaticSourceAndNoPasswordSet_ReportsThatNoPasswordHasBeenSet()
    {
        var configuration = new SyncRuleInitialPassword { Enabled = true, Source = InitialPasswordSource.Static };

        var problems = _server.AssessConfiguration(configuration, null);

        Assert.Multiple(() =>
        {
            Assert.That(problems, Has.Count.EqualTo(1));
            Assert.That(problems[0], Does.Contain("no password has been set"));
        });
    }

    /// <summary>
    /// A stored password needs no re-checking: it was assessed when it was set, and it is never decrypted to be
    /// looked at again.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithAStaticSourceAndAPasswordSet_FindsNothingToFix()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPasswordEncryptedValue = "an-encrypted-value",
            // Deliberately unsatisfiable, and deliberately ignored: no password is generated for a static source.
            CustomPolicy = UnsatisfiablePolicy()
        };

        Assert.That(_server.AssessConfiguration(configuration, null), Is.Empty);
    }

    /// <summary>
    /// A generator configuration that cannot produce a password is reported in the generator's own words, so the
    /// administrator is told what to change rather than that something is wrong.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithAnUnsatisfiableCustomPolicy_ReportsWhatTheGeneratorFound()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = UnsatisfiablePolicy()
        };

        Assert.That(_server.AssessConfiguration(configuration, null), Is.Not.Empty);
    }

    /// <summary>
    /// The discovered source generates from settings derived from the target, not from the custom policy sitting
    /// beside it, so an unusable custom policy must not be reported against a rule that is not using it.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithTheDiscoveredSource_IgnoresTheCustomPolicyItIsNotUsing()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Discovered,
            CustomPolicy = UnsatisfiablePolicy()
        };

        Assert.That(_server.AssessConfiguration(configuration, null), Is.Empty);
    }

    /// <summary>
    /// The target's own policy is part of the question: a configuration JIM can satisfy but the target would
    /// refuse parks every account just as surely as one JIM cannot satisfy at all.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithACustomPolicyTheTargetWouldRefuse_ReportsIt()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy { Style = PasswordGenerationStyle.RandomCharacters, Length = 12 }
        };

        var problems = _server.AssessConfiguration(configuration, new ConnectedSystemPasswordPolicy { MinimumLength = 20 });

        Assert.That(problems, Is.Not.Empty, "the target requires more characters than this configuration can produce");
    }

    /// <summary>
    /// A passphrase of no words cannot be generated, and is the shortest way to express an unsatisfiable
    /// configuration without depending on any particular target policy.
    /// </summary>
    private static PasswordGenerationPolicy UnsatisfiablePolicy() =>
        new() { Style = PasswordGenerationStyle.Words, WordCount = 0 };

    #endregion

    #region Helper Methods

    /// <summary>
    /// Stages one account as owed an initial password, and returns the account.
    /// </summary>
    private async Task<ConnectedSystemObject> StageOutstandingAsync(int connectedSystemId = ConnectedSystemId, DateTime? expiresAt = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Status = ConnectedSystemObjectStatus.Normal
        };
        _syncRepo.SeedConnectedSystemObject(cso);

        await _syncRepo.StageInitialPasswordsAsync([
            new PendingInitialPassword
            {
                ConnectedSystemObjectId = cso.Id,
                ConnectedSystemId = connectedSystemId,
                SyncRuleId = SyncRuleId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt
            }
        ]);

        return cso;
    }

    /// <summary>
    /// A Connector that can set passwords, answering every set with the given result.
    /// </summary>
    private static Mock<IConnector> MockConnector(PasswordSetResult result, Action<string>? capturePassword = null)
    {
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns("Test Password Connector");

        var passwordConnector = connector.As<IConnectorPasswordManagement>();
        passwordConnector.Setup(c => c.SupportedExpiryBehaviours).Returns(Enum.GetValues<PasswordExpiryBehaviour>());
        passwordConnector.Setup(c => c.SetPasswordAsync(It.IsAny<ConnectedSystemObject>(), It.IsAny<string>(), It.IsAny<PasswordSetOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectedSystemObject _, string password, PasswordSetOptions _, CancellationToken _) =>
            {
                capturePassword?.Invoke(password);
                return result;
            });

        return connector;
    }

    #endregion
}
