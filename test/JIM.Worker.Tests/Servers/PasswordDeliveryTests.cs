// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Connectors.Mock;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Services;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Delivering the password changes queued against a Connected System (#1119).
/// <para>
/// The pass exists to be safe rather than clever, and the tests here pin what "safe" means: a delivered password
/// leaves no row behind, a refused one keeps everything an administrator needs to act on, a change is never
/// attempted after its window has passed, and nothing about a password reaches a log or an Activity.
/// </para>
/// </summary>
[TestFixture]
public class PasswordDeliveryTests
{
    private const int ConnectedSystemId = 3;
    private const int UserObjectTypeId = 200;
    private const int SyncRuleId = 500;
    private const string ClaimedBy = "worker-test-1a2b3c4d";

    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private TestCredentialProtection _protection = null!;
    private List<Activity> _createdActivities = null!;
    private PasswordSynchronisationServer _server = null!;
    private MockCallConnector _connector = null!;
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemPasswordSynchronisation _configuration = null!;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _protection = new TestCredentialProtection();
        _createdActivities = [];
        _connector = new MockCallConnector();

        _configuration = new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = ConnectedSystemId,
            Enabled = true,
            TargetObjectTypeId = UserObjectTypeId,
            MaxRetries = 3,
            RetryBackoffBase = TimeSpan.FromMinutes(5)
        };

        _connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Corporate AD",
            PasswordSynchronisation = _configuration,
            SettingValues = []
        };

        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync([]);

        _server = new PasswordSynchronisationServer(
            _syncRepository,
            () => _connectedSystemRepository.Object,
            () => new Mock<JIM.Data.Repositories.IActivityRepository>().Object,
            () => _protection,
            // These fixtures never reach a Connector: they exercise queueing and one-change delivery with a
            // Connector handed in directly. Resolving one here would be answering a question they do not ask.
            _ => throw new NotSupportedException("This fixture does not resolve Connectors."),
            (activity, initiatedBy, initiatedByApiKey) =>
            {
                // The real Activity server refuses an Activity attributed to nobody, and a fake that accepted one
                // is why #1529 hid here for the whole of delivery's life.
                if (initiatedBy == null && initiatedByApiKey == null)
                    throw new InvalidOperationException(
                        "Activity must be attributed to a security principal. InitiatedByType has not been set.");
                _createdActivities.Add(activity);
                return Task.CompletedTask;
            },
            activity =>
            {
                activity.InitiatedByType = ActivityInitiatorType.System;
                activity.InitiatedByName = "System";
                _createdActivities.Add(activity);
                return Task.CompletedTask;
            },
            // Mirrors what the real Activity server does on completion, because these fixtures assert on the
            // outcome an administrator would read: an Activity left InProgress forever is a defect this harness
            // has to be able to see.
            activity =>
            {
                activity.Status = ActivityStatus.Complete;
                return Task.CompletedTask;
            },
            (activity, errorMessage) =>
            {
                activity.Status = ActivityStatus.FailedWithError;
                activity.ErrorMessage = errorMessage;
                return Task.CompletedTask;
            },
            activity =>
            {
                activity.Status = ActivityStatus.CompleteWithWarning;
                return Task.CompletedTask;
            },
            new PasswordGeneratorService(),
            () => _protection);
    }

    private async Task<PendingPasswordChange> QueueAsync(
        string password = "a-password",
        Guid? connectedSystemObjectId = null,
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = connectedSystemObjectId ?? Guid.NewGuid(),
            EncryptedPassword = _protection.ProtectPassword(password)!,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await _syncRepository.QueuePasswordChangesAsync([change]);
        return change;
    }

    private void ArrangeAccount(PendingPasswordChange change, int typeId = UserObjectTypeId) =>
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId))
            .ReturnsAsync([
                new ConnectedSystemObject
                {
                    Id = change.ConnectedSystemObjectId ?? Guid.NewGuid(),
                    ConnectedSystemId = ConnectedSystemId,
                    TypeId = typeId
                }
            ]);

    /// <summary>
    /// An administrator's explicit set of a named account (#1635): the row carries the account and the enable
    /// decision, and is delivered whether or not the system is taking propagated passwords.
    /// </summary>
    private async Task<PendingPasswordChange> QueueExplicitAsync(bool? enableAccount = null, DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = Guid.NewGuid(),
            EncryptedPassword = _protection.ProtectPassword("a-password")!,
            Origin = PendingPasswordChangeOrigin.Explicit,
            EnableAccount = enableAccount,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await _syncRepository.QueuePasswordChangesAsync([change]);
        return change;
    }

    /// <summary>
    /// A Provisioned row for a newly provisioned account's first password (#1697): seeds the
    /// Synchronisation Rule the row's <see cref="PendingPasswordChange.SyncRuleId"/> resolves against and the
    /// Connected System its discovered password policy is read from, then queues a row carrying no password of
    /// its own, matching what export staging produces.
    /// </summary>
    private async Task<(PendingPasswordChange Change, SyncRule Rule)> QueueProvisionedAsync(
        int syncRuleId,
        Action<SyncRuleInitialPassword>? configure = null,
        DateTime? createdAt = null,
        bool arrangeAccount = true)
    {
        var initialPassword = new SyncRuleInitialPassword
        {
            SyncRuleId = syncRuleId,
            Enabled = true,
            Source = InitialPasswordSource.Discovered,
            ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
            EnableAccount = true
        };
        configure?.Invoke(initialPassword);

        var rule = new SyncRule { Id = syncRuleId, Name = $"Provisioning rule {syncRuleId}", InitialPassword = initialPassword };
        _syncRepository.SeedSyncRule(rule);
        _syncRepository.SeedConnectedSystem(_connectedSystem);

        var now = createdAt ?? DateTime.UtcNow;
        var accountId = Guid.NewGuid();
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = accountId,
            EncryptedPassword = null,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            SyncRuleId = syncRuleId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await _syncRepository.QueuePasswordChangesAsync([change]);

        if (arrangeAccount)
            _connectedSystemRepository
                .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId))
                .ReturnsAsync([
                    new ConnectedSystemObject { Id = accountId, ConnectedSystemId = ConnectedSystemId, TypeId = UserObjectTypeId }
                ]);

        return (change, rule);
    }

    /// <summary>
    /// Regression for #1529. The outcome Activity was created with neither a person nor an API key against it,
    /// which the Activity server refuses ("Activity must be attributed to a security principal"). The refusal
    /// threw out of the delivery pass, so no outcome could ever be recorded and the change was retried for ever;
    /// the password reached the directory and JIM then crashed recording that it had.
    /// <para>
    /// Delivery runs unattended, minutes or days after somebody queued the change, so there is no person to
    /// attribute it to and JIM itself is the honest principal. The parent Activity still names whoever made the
    /// password change.
    /// </para>
    /// </summary>
    [Test]
    public async Task Deliver_RecordsTheOutcomeAttributedToTheSystemAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        _createdActivities.Clear();

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var outcome = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.PasswordSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1), "the pass must not throw recording what it did");
            Assert.That(outcome, Is.Not.Null, "the outcome Activity is all that survives once the queue row is deleted");
            Assert.That(outcome!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        }
    }

    [Test]
    public async Task Deliver_OnSuccess_RemovesTheChangeFromTheQueueAsync()
    {
        // Requirement 11: success deletes the row. Keeping it would hold an encrypted password long after
        // anything needed it, to answer a question the Activity already answers.
        var change = await QueueAsync();
        ArrangeAccount(change);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_SendsTheDecryptedPasswordToTheConnectorAsync()
    {
        var change = await QueueAsync("Correct-Horse-Battery-Staple");
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        // The mock records the length rather than the value, deliberately, so this asserts what it can: the
        // connector was handed a password of the right size rather than the ciphertext.
        Assert.That(_connector.PasswordSetAttempts.Single().PasswordLength,
            Is.EqualTo("Correct-Horse-Battery-Staple".Length));
    }

    [Test]
    public async Task Deliver_OpensAndClosesThePasswordChannelExactlyOnceAsync()
    {
        // Once per pass rather than once per change: a directory bind is expensive, and the initial-password
        // pass sets the same precedent.
        var first = await QueueAsync();
        var second = await QueueAsync();
        ArrangeAccount(first);
        ArrangeAccount(second);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Has.Count.EqualTo(2));
            Assert.That(_connector.PasswordConnectionOpen, Is.False, "The channel is closed when the pass ends.");
        }
    }

    [Test]
    public async Task Deliver_CarriesTheChangesExpiryBehaviourToTheConnectorAsync()
    {
        var change = await QueueAsync();
        change.ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires;
        await _syncRepository.RecordPasswordChangeAttemptsAsync([change]);
        _syncRepository.PendingPasswordChanges[change.Id].ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires;
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.ExpiryBehaviour,
            Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
    }

    [Test]
    public async Task Deliver_NeverEnablesAnAccountAsync()
    {
        // Enabling belongs to provisioning, which is the initial password's job. A synchronised password reaches
        // accounts an administrator may have disabled deliberately, and re-enabling one would undo that silently.
        var change = await QueueAsync();
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.Null);
    }

    [Test]
    public async Task Deliver_WithATransientFailure_KeepsTheChangeAndSchedulesARetryAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ =>
            PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Server unavailable"));

        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryingCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
            Assert.That(stored.NextRetryAt, Is.EqualTo(now.AddMinutes(5)));
            Assert.That(stored.TargetMessage, Is.EqualTo("Server unavailable"));
        }
    }

    [Test]
    public async Task Deliver_WithAPolicyRejection_ParksWithTheTargetsOwnWordsAsync()
    {
        // Requirement 13. The password came from the person, so JIM has nothing else to send; another attempt
        // would present the same password and collect the same refusal.
        var change = await QueueAsync();
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ =>
            PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Password does not meet complexity requirements"));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(stored.NextRetryAt, Is.Null);
            Assert.That(stored.TargetMessage, Is.EqualTo("Password does not meet complexity requirements"),
                "The target's own words are the most useful thing an administrator can be shown.");
        }
    }

    [Test]
    public async Task Deliver_WhenTheAccountDoesNotExistYet_RetriesRatherThanParkingAsync()
    {
        // Resolved Decision 2: the provisioning race resolves itself while the change is still in its window.
        var change = await QueueAsync(connectedSystemObjectId: null);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryingCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(_connector.PasswordSetAttempts, Is.Empty, "Nothing to set a password on yet.");
        }
    }

    [Test]
    public async Task Deliver_WhenTheAccountAppearsLater_ResolvesItAndDeliversAsync()
    {
        // The other half of the race: the change was queued with no account, and provisioning has since caught up.
        var change = await QueueAsync(connectedSystemObjectId: null);
        var accountId = Guid.NewGuid();
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId))
            .ReturnsAsync([
                new ConnectedSystemObject { Id = accountId, ConnectedSystemId = ConnectedSystemId, TypeId = UserObjectTypeId }
            ]);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_connector.PasswordSetAttempts.Single().ConnectedSystemObjectId, Is.EqualTo(accountId));
        }
    }

    [Test]
    public async Task Deliver_ExpiresOverdueChangesBeforeAttemptingAnythingAsync()
    {
        // Expiry runs first so a change on its way out is not attempted, and its attempt count not inflated, on
        // the pass that retires it.
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var overdue = await QueueAsync(createdAt: now.AddDays(-8));
        ArrangeAccount(overdue);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExpiredCount, Is.EqualTo(1));
            Assert.That(result.DeliveredCount, Is.Zero);
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_WithAConnectorThatCannotSetPasswords_LeavesTheQueueAloneAsync()
    {
        // The capability can arrive with a Connector upgrade, so the queued work waits rather than being failed.
        var change = await QueueAsync();
        ArrangeAccount(change);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, new MockFileConnector(), ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConnectorCannotSetPasswords, Is.True);
            Assert.That(_syncRepository.PendingPasswordChanges.Values.Single().AttemptCount, Is.Zero,
                "Nothing was attempted, so nothing is counted against the change.");
        }
    }

    [Test]
    public async Task Deliver_WithNoConfiguration_DoesNothingAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        _connectedSystem.PasswordSynchronisation = null;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasSomethingToReport, Is.False);
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_WhileDisabled_DeliversNothingButKeepsTheQueueAsync()
    {
        // Requirement 2: a configured but disabled system accumulates rather than discarding.
        var change = await QueueAsync();
        ArrangeAccount(change);
        _configuration.Enabled = false;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.Zero);
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(_syncRepository.PendingPasswordChanges, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Deliver_RecordsAChildActivityPerOutcomeAsync()
    {
        // Requirement 23: the parent Activity records the change, a child records what each system said.
        var delivered = await QueueAsync();
        var refused = await QueueAsync();
        ArrangeAccount(delivered);
        ArrangeAccount(refused);
        _connector.WithPasswordSetResult(target =>
            target.Id == refused.ConnectedSystemObjectId
                ? PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Too short")
                : PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_createdActivities, Has.Count.EqualTo(2));
            Assert.That(_createdActivities.Select(a => a.ParentActivityId),
                Is.EquivalentTo(new Guid?[] { delivered.ActivityId, refused.ActivityId }),
                "Each outcome hangs off the Activity for the change that produced it.");
            Assert.That(_createdActivities.Select(a => a.TargetType),
                Is.All.EqualTo(ActivityTargetType.PasswordSynchronisation));
            Assert.That(_createdActivities.Select(a => a.ConnectedSystemId), Is.All.EqualTo(ConnectedSystemId));
        }
    }

    /// <summary>
    /// The outcome Activity has to reach a terminal state. Creating one sets it InProgress, so an outcome that
    /// is never completed sits in the Activities list looking like work still under way, forever, for a delivery
    /// that finished seconds after it started.
    /// </summary>
    [Test]
    public async Task Deliver_Success_CompletesTheOutcomeActivityAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_createdActivities.Single().Status, Is.EqualTo(ActivityStatus.Complete));
    }

    /// <summary>
    /// The warning an OpenLDAP or 389 Directory Server target returns when asked to require a change at the next
    /// sign-in: the password is set, and expiry is left to the directory's own policy (#2159).
    /// </summary>
    private const string ExpiryDowngradeWarning =
        "'Require a change at next sign-in' is an Active Directory behaviour with no portable equivalent on this directory, " +
        "so the password will expire according to whichever password policy the directory applies to the account.";

    private static PasswordSetResult ExpiryDowngrade(string warning = ExpiryDowngradeWarning) =>
        PasswordSetResult.SucceededWithExpiryDowngrade(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, warning);

    /// <summary>
    /// Regression for #2159. A Connector that sets the password but cannot apply the requested expiry reports it on
    /// the result, and the delivery used to read only Success, so the outcome was a plain completion: the helpdesk
    /// was told a change would be demanded at the next sign-in when it would not. The password is set, so it is
    /// not a failure, but it is completed with a warning saying what was applied, in the Connector's words.
    /// </summary>
    [Test]
    public async Task Deliver_ConnectorCouldNotApplyTheExpiry_CompletesTheOutcomeWithAWarningAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ => ExpiryDowngrade());

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1), "the password is set, so the change is delivered and leaves the queue");
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.Message, Does.StartWith("Password set on Corporate AD"));
            Assert.That(activity.Message, Does.Contain("expires according to"), "the message names what was applied");
            Assert.That(activity.Message, Does.Contain(ExpiryDowngradeWarning), "and carries the Connector's own words");
        }
    }

    [Test]
    public async Task Deliver_ConnectorCouldNotApplyTheExpiry_CountsItOnTheRunResultAsync()
    {
        var downgraded = await QueueAsync();
        var honoured = await QueueAsync();
        ArrangeAccount(downgraded);
        ArrangeAccount(honoured);
        _connector.WithPasswordSetResult(target =>
            target.Id == downgraded.ConnectedSystemObjectId
                ? ExpiryDowngrade()
                : PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(2));
            Assert.That(result.ExpiryBehaviourNotHonouredCount, Is.EqualTo(1));
            Assert.That(_createdActivities.Select(a => a.Status),
                Is.EquivalentTo(new[] { ActivityStatus.Complete, ActivityStatus.CompleteWithWarning }),
                "a clean success still completes plainly");
        }
    }

    /// <summary>
    /// The Connector's warning goes onto an Activity, which the portal and the API show. A Connector is under no
    /// obligation to keep the password out of its own words, so one that echoes it has the warning withheld.
    /// </summary>
    [Test]
    public async Task Deliver_ConnectorWarningContainingThePassword_IsWithheldFromTheActivityAsync()
    {
        var change = await QueueAsync("Correct-Horse-Battery-Staple");
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ => ExpiryDowngrade("Could not apply expiry to the password Correct-Horse-Battery-Staple."));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That($"{activity.Message} {activity.ErrorMessage}", Does.Not.Contain("Correct-Horse"));
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_ConnectorCouldNotApplyTheExpiry_CompletesWithAWarningAsync()
    {
        await QueueProvisionedAsync(SyncRuleId);
        _connector.WithPasswordSetResult(_ => ExpiryDowngrade());

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExpiryBehaviourNotHonouredCount, Is.EqualTo(1));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.Message, Does.StartWith("Initial password set on Corporate AD"));
            Assert.That(activity.Message, Does.Contain(ExpiryDowngradeWarning));
        }
    }

    /// <summary>
    /// A refusal is recorded as a failure, not merely described in prose (requirement 23). A Message saying
    /// "Password not set on..." reads correctly and is invisible to everything that counts, filters or alerts on
    /// outcomes, which is most of what an audit record is for.
    /// </summary>
    [Test]
    public async Task Deliver_Refusal_RecordsTheOutcomeAsAFailureAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ =>
            PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Too short"));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Does.Contain("Too short"),
                "the target's own words are what say where the remedy lives");
        }
    }

    [Test]
    public async Task Deliver_Refusal_NeverPutsThePasswordInTheErrorMessageAsync()
    {
        var change = await QueueAsync("Correct-Horse-Battery-Staple");
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ =>
            PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Too short"));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_createdActivities.Single().ErrorMessage ?? string.Empty,
            Does.Not.Contain("Correct-Horse").And.Not.Contain(change.EncryptedPassword));
    }

    [Test]
    public async Task Deliver_NeverRecordsThePasswordOnAnActivityAsync()
    {
        var change = await QueueAsync("Correct-Horse-Battery-Staple");
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        var text = $"{activity.TargetName} {activity.Message} {activity.TargetContext}";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Not.Contain("Correct-Horse").And.Not.Contain("Battery"));
            Assert.That(text, Does.Not.Contain(change.EncryptedPassword),
                "Not the ciphertext either: it decrypts with the deployment's own key ring.");
        }
    }

    [Test]
    public async Task Deliver_WithAChangeNotYetDue_SkipsItAsync()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var change = await QueueAsync(createdAt: now);
        ArrangeAccount(change);
        change.NextRetryAt = now.AddHours(1);
        await _syncRepository.RecordPasswordChangeAttemptsAsync([change]);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasSomethingToReport, Is.False);
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_WhenRetriesAreExhausted_ParksTheChangeAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        change.AttemptCount = 3;
        await _syncRepository.RecordPasswordChangeAttemptsAsync([change]);
        _connector.WithPasswordSetResult(_ =>
            PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Still unavailable"));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges.Values.Single().Status,
                Is.EqualTo(PendingPasswordChangeStatus.Parked));
        }
    }

    #region explicit sets (#1635, decision D1)

    [Test]
    public async Task Deliver_ExplicitRowOnADisabledSystem_IsDeliveredAsync()
    {
        // The administrator named the account; the system being paused for propagation does not hold it.
        _configuration.Enabled = false;
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_connector.PasswordSetAttempts.Single().ConnectedSystemObjectId, Is.EqualTo(change.ConnectedSystemObjectId));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_MixedOriginsOnADisabledSystem_DeliversExplicitAndProvisionedRowsAsync()
    {
        _configuration.Enabled = false;
        var propagated = await QueueAsync();
        var explicitSet = await QueueExplicitAsync();
        var (provisioned, _) = await QueueProvisionedAsync(SyncRuleId);
        ArrangeAccount(propagated);
        ArrangeAccount(explicitSet);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var remaining = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(2));
            Assert.That(_connector.PasswordSetAttempts.Select(a => a.ConnectedSystemObjectId),
                Is.EquivalentTo(new[] { explicitSet.ConnectedSystemObjectId, provisioned.ConnectedSystemObjectId }));
            Assert.That(remaining.Id, Is.EqualTo(propagated.Id), "The propagated change is held, untouched, until the system is switched on.");
            Assert.That(remaining.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(remaining.AttemptCount, Is.Zero);
        }
    }

    [Test]
    public async Task Deliver_ExplicitRowOnAnUnconfiguredSystem_IsDeliveredAsync()
    {
        _connectedSystem.PasswordSynchronisation = null;
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    /// <summary>
    /// With no configuration to read a retry policy from, an explicit set retries under JIM's defaults. The
    /// default instance is built for the lane and never persisted: nothing here configures Password
    /// Synchronisation on the administrator's behalf.
    /// </summary>
    [Test]
    public async Task Deliver_ExplicitRowOnAnUnconfiguredSystem_RetriesUnderTheDefaultPolicyAsync()
    {
        _connectedSystem.PasswordSynchronisation = null;
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ => PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Server unavailable"));

        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryingCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.NextRetryAt, Is.EqualTo(now + ConnectedSystemPasswordSynchronisation.DefaultRetryBackoffBase));
            Assert.That(_connectedSystem.PasswordSynchronisation, Is.Null, "The lane must not leave a configuration behind.");
        }
    }

    /// <summary>
    /// An explicit set is aimed at the account the administrator named, not at whichever account the system's
    /// configuration nominates; the two can differ where a person holds accounts of more than one type.
    /// </summary>
    [Test]
    public async Task Deliver_ExplicitRow_UsesItsOwnAccountRatherThanTheConfiguredTypeAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change, typeId: 999);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_connector.PasswordSetAttempts.Single().ConnectedSystemObjectId, Is.EqualTo(change.ConnectedSystemObjectId));
        }
    }

    [Test]
    public async Task Deliver_ExplicitRowWhoseAccountIsGone_ParksWithTargetObjectNotFoundAsync()
    {
        // Unlike a propagated change, which waits for provisioning to catch up, an explicit set has nothing to
        // wait for: the account it was for is gone, and only a person can decide what to do about that.
        var change = await QueueExplicitAsync();

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.TargetObjectNotFound));
            Assert.That(stored.TargetMessage, Does.Contain("Corporate AD"));
            Assert.That(stored.NextRetryAt, Is.Null);
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Does.Contain("no longer exists"));
        }
    }

    /// <summary>
    /// Defensive: <see cref="PendingPasswordChange.EncryptedPassword"/> is nullable so a Provisioned row can carry
    /// none, but an Explicit or Propagated row must always have one; nothing that queues either kind ever leaves
    /// it null. If it ever does, there is nothing to send and no number of attempts changes that.
    /// </summary>
    [Test]
    public async Task Deliver_ExplicitRowWithNoPassword_ParksAsConfigurationFaultAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);
        _syncRepository.PendingPasswordChanges[change.Id].EncryptedPassword = null;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(_connector.PasswordSetAttempts, Is.Empty, "The Connector must never be asked to set nothing.");
        }
    }

    [Test]
    public async Task Deliver_ExplicitRow_CarriesTheEnableDecisionToTheConnectorAsync()
    {
        var change = await QueueExplicitAsync(enableAccount: true);
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
    }

    [Test]
    public async Task Deliver_ExplicitRow_RecordsTheSameChildActivityShapeAsAPropagatedOneAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword));
            Assert.That(activity.ParentActivityId, Is.EqualTo(change.ActivityId));
            Assert.That(activity.ConnectedSystemId, Is.EqualTo(ConnectedSystemId));
            Assert.That(activity.ConnectedSystemObjectId, Is.EqualTo(change.ConnectedSystemObjectId));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task Deliver_OnADisabledSystem_ExpiresEverythingButPropagatedRowsAsync()
    {
        // The held propagated change is left exactly as it was, to be expired or delivered by the first lane
        // after the system is switched on, as it always has been.
        _configuration.Enabled = false;
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var propagated = await QueueAsync(createdAt: now.AddDays(-8));
        var explicitSet = await QueueExplicitAsync(createdAt: now.AddDays(-8));
        var (provisioned, _) = await QueueProvisionedAsync(SyncRuleId, createdAt: now.AddDays(-8));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExpiredCount, Is.EqualTo(2));
            Assert.That(_syncRepository.PendingPasswordChanges[explicitSet.Id].Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
            Assert.That(_syncRepository.PendingPasswordChanges[provisioned.Id].Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
            Assert.That(_syncRepository.PendingPasswordChanges[propagated.Id].Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
        }
    }

    #endregion

    #region provisioned rows (#1697)

    [Test]
    public async Task Deliver_ProvisionedRow_GeneratesAPasswordFromTheRuleAndSetsItAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().ConnectedSystemObjectId, Is.EqualTo(change.ConnectedSystemObjectId));
            Assert.That(_connector.PasswordSetAttempts.Single().PasswordLength, Is.GreaterThan(0), "A password was generated and sent.");
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_StaticSource_SendsTheDecryptedStaticPasswordAsync()
    {
        const string staticPassword = "Static-Provision-42";
        await QueueProvisionedAsync(SyncRuleId, ip =>
        {
            ip.Source = InitialPasswordSource.Static;
            ip.StaticPasswordEncryptedValue = _protection.Protect(staticPassword);
        });

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().PasswordLength, Is.EqualTo(staticPassword.Length));
    }

    [Test]
    public async Task Deliver_ProvisionedRow_CarriesTheRulesExpiryBehaviourAndEnableDecisionAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId, ip => ip.ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var options = _connector.PasswordSetAttempts.Single().Options;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(options.EnableAccount, Is.True, "Nothing sets the enabled state, so the rule's Enable Account decides.");
            Assert.That(change.ExpiryBehaviour, Is.Not.EqualTo(PasswordExpiryBehaviour.NeverExpires),
                "The row's own placeholder value must never be what reaches the Connector.");
        }
    }

    #region the account's enabled state (#2064)

    /// <summary>
    /// Enable Account off leaves the account as it was created rather than disabling it: an account nothing sets the
    /// enabled state of is created disabled by the directory anyway, and a late delivery must not disable one a
    /// separate process has since enabled.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_RuleOffAndNoFlowSetsTheEnabledState_LeavesTheAccountAsCreatedAsync()
    {
        await QueueProvisionedAsync(SyncRuleId, ip => ip.EnableAccount = false);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.Null);
    }

    /// <summary>
    /// The reported bug: the Attribute Flow creates a pre-hire or an archived joiner disabled, and the rule's Enable
    /// Account enabled it with a password JIM knows. The export wrote the enabled state, so the flow decides.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_EnabledStateSetByAttributeFlow_LeavesItToTheFlowAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.Null);
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(result.EnabledStateLeftToAttributeFlowCount, Is.EqualTo(1), "the decision is counted, never silent");
        }
    }

    /// <summary>
    /// Active Directory: JIM created the account disabled only until a password existed, and the Attribute Flow still
    /// asks for it enabled (#2039). The delivery completes that enable, whatever the rule's Enable Account says.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_HeldEnable_EnablesWhateverTheRuleSaysAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId, ip => ip.EnableAccount = false);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
            Assert.That(result.WithheldEnablesCompletedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A leaver whose held enable was withdrawn or released is no longer held: only an enable still awaiting its
    /// password counts, so the delivery leaves the account disabled.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_HeldEnableNoLongerAwaitingThePassword_LeavesItToTheFlowAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value, PendingExportAttributeChangeStatus.Pending);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.Null);
    }

    [Test]
    public async Task Deliver_PropagatedRow_HeldEnable_EnablesAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
    }

    [Test]
    public async Task Deliver_ExplicitRowWithNoChoice_HeldEnable_EnablesAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
    }

    [Test]
    public async Task Deliver_ExplicitRowWithAChoice_TheChoiceWinsOverAHeldEnableAsync()
    {
        var change = await QueueExplicitAsync(enableAccount: false);
        ArrangeAccount(change);
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.False);
    }

    /// <summary>
    /// A lane claims its batch up front and then works through it one account at a time, so an export can mark a
    /// claimed row (a leaver disabled moments ago) before the lane reaches it. The mark is read afresh at the attempt,
    /// not taken from the claim, or the delivery would re-enable the leaver.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_MarkedAfterTheClaim_LeavesItToTheFlowAsync()
    {
        var now = DateTime.UtcNow;
        var (first, _) = await QueueProvisionedAsync(SyncRuleId, createdAt: now.AddMinutes(-2));
        var (second, _) = await QueueProvisionedAsync(SyncRuleId, createdAt: now.AddMinutes(-1));
        _connector.WithPasswordSetResult(account =>
        {
            // The leaver's export lands while the lane is busy with the first account.
            if (account.Id == first.ConnectedSystemObjectId)
                _syncRepository.PendingPasswordChanges[second.Id].EnabledStateSetByAttributeFlow = true;
            return PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn);
        });

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var attempts = _connector.PasswordSetAttempts.ToDictionary(a => a.ConnectedSystemObjectId, a => a.Options.EnableAccount);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts[first.ConnectedSystemObjectId!.Value], Is.True, "nothing set the first account's enabled state");
            Assert.That(attempts[second.ConnectedSystemObjectId!.Value], Is.Null, "marked after the claim, before its attempt");
        }
    }

    /// <summary>
    /// The hold is read live at every attempt; a read that fails never defaults to a decision, because guessing
    /// "not held" could leave an account disabled and guessing "held" could enable one. The attempt is kept as a
    /// transient failure and nothing is sent.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_HoldCannotBeRead_KeepsTheChangeWithoutSendingAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        _syncRepository.FailInitialPasswordHoldProbeWith = new InvalidOperationException("The database is unavailable.");

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges[change.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty, "nothing is sent on a guess");
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
            Assert.That(result.RetryingCount, Is.EqualTo(1));
        }
    }

    #region "Password never expires" left to whoever owns userAccountControl (#2159)

    /// <summary>
    /// The reported bug: an Attribute Flow asserts "Password never expires" (66048) and the Initial Password's expiry
    /// used to clear it. Where an export wrote the account's flags, the flow owns both, so the delivery tells the
    /// Connector to leave the flag, and counts that it did.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_FlagsWrittenByAnExport_LeavesPasswordNeverExpiresToTheFlowAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId, ip => ip.ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var options = _connector.PasswordSetAttempts.Single().Options;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.LeavePasswordNeverExpiresAsIs, Is.True);
            Assert.That(options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy),
                "the expiry still travels, for the parts of it that belong to the password");
            Assert.That(result.PasswordNeverExpiresLeftToAttributeFlowCount, Is.EqualTo(1), "the decision is counted, never silent");
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_NothingWritesTheFlags_SendsTheRulesExpiryWholeAsync()
    {
        await QueueProvisionedAsync(SyncRuleId, ip => ip.ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var options = _connector.PasswordSetAttempts.Single().Options;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.LeavePasswordNeverExpiresAsIs, Is.False, "nothing else owns the flag, so the rule's expiry sets it");
            Assert.That(options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(result.PasswordNeverExpiresLeftToAttributeFlowCount, Is.Zero);
        }
    }

    /// <summary>
    /// The mark is read afresh at the attempt for the flag as for the enabled state: an export landing while the lane
    /// works through the accounts ahead of this one takes the flag with it.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_MarkedAfterTheClaim_LeavesPasswordNeverExpiresToTheFlowAsync()
    {
        var now = DateTime.UtcNow;
        var (first, _) = await QueueProvisionedAsync(SyncRuleId, createdAt: now.AddMinutes(-2));
        var (second, _) = await QueueProvisionedAsync(SyncRuleId, createdAt: now.AddMinutes(-1));
        _connector.WithPasswordSetResult(account =>
        {
            if (account.Id == first.ConnectedSystemObjectId)
                _syncRepository.PendingPasswordChanges[second.Id].EnabledStateSetByAttributeFlow = true;
            return PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn);
        });

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var attempts = _connector.PasswordSetAttempts.ToDictionary(a => a.ConnectedSystemObjectId, a => a.Options.LeavePasswordNeverExpiresAsIs);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts[first.ConnectedSystemObjectId!.Value], Is.False);
            Assert.That(attempts[second.ConnectedSystemObjectId!.Value], Is.True);
        }
    }

    /// <summary>
    /// A propagated password reaches accounts an administrator may have exempted from expiry on purpose; clearing the
    /// flag as a side effect of a password change elsewhere would undo that silently.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRow_LeavesPasswordNeverExpiresAsIsAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.LeavePasswordNeverExpiresAsIs, Is.True);
    }

    /// <summary>
    /// Completing an enable withheld until a password existed (#2039) touches the enabled state only: the flow's
    /// "Password never expires" stays (66050 becomes 66048, not 512).
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRow_HeldEnable_EnablesAndLeavesPasswordNeverExpiresAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var options = _connector.PasswordSetAttempts.Single().Options;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.EnableAccount, Is.True);
            Assert.That(options.LeavePasswordNeverExpiresAsIs, Is.True);
        }
    }

    [Test]
    public async Task Deliver_ExplicitRow_SendsTheAdministratorsExpiryWholeAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.LeavePasswordNeverExpiresAsIs, Is.False,
            "an administrator who named the account chose its expiry, flag and all");
    }

    /// <summary>
    /// A row queued with Never expires and no named accounts (one queued before such requests were refused) is left
    /// alone: the flag stays as the account has it, which is not a failure, and the Activity says why the expiry asked
    /// for was not applied.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowQueuedWithNeverExpires_LeavesTheFlagAndSaysSoAsync()
    {
        var change = await QueueAsync();
        _syncRepository.PendingPasswordChanges[change.Id].ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires;
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ => PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.LeavePasswordNeverExpiresAsIs, Is.True);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.Message, Does.Contain("Password never expires"));
            Assert.That(activity.Message, Does.Contain("as each account has it"));
        }
    }

    /// <summary>
    /// The mirror of the reported bug: the rule asks for Never expires and the flow asserts 512. The flow wins, which is
    /// not a failure to apply anything, but the Activity says so and says how to have such passwords never expire.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_FlowLeavesPasswordNeverExpiresClear_SaysSoWithoutAWarningAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId, ip => ip.ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;
        _connector.WithPasswordSetResult(_ => PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.Message, Does.Contain("left to the export that writes userAccountControl"));
            Assert.That(activity.Message, Does.Contain("65536"), "names how to have the flow set the flag");
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_FlowSetsPasswordNeverExpires_SaysTheFlowDecidedAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId, ip => ip.ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;
        _connector.WithPasswordSetResult(_ => PasswordSetResult.Succeeded(PasswordExpiryBehaviour.NeverExpires));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.Message, Does.Contain("left to the export that writes userAccountControl"));
            Assert.That(activity.Message, Does.Contain("the password never expires"));
        }
    }

    /// <summary>
    /// Require a change at the next sign-in, under a flow that keeps "Password never expires": the Connector still
    /// writes pwdLastSet = 0, but Active Directory ignores it, so the delivery completes with a warning. For a Static
    /// Initial Password the consequence is worse, and the Activity says so: every account keeps the one shared password.
    /// </summary>
    [TestCase(InitialPasswordSource.Discovered, false)]
    [TestCase(InitialPasswordSource.Static, true)]
    public async Task Deliver_ProvisionedRow_RequireChangeDefeatedByTheFlowsPasswordNeverExpires_CompletesWithAWarningAsync(
        InitialPasswordSource source, bool namesTheSharedPassword)
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId, ip =>
        {
            ip.Source = source;
            if (source == InitialPasswordSource.Static)
                ip.StaticPasswordEncryptedValue = _protection.Protect("Static-Provision-42");
        });
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;
        _connector.WithPasswordSetResult(_ => PasswordSetResult.SucceededWithExpiryDowngrade(PasswordExpiryBehaviour.NeverExpires,
            "Active Directory does not ask for a new password while that flag is set."));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(result.ExpiryBehaviourNotHonouredCount, Is.EqualTo(1));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.Message, Does.Contain("the password never expires"));
            Assert.That(activity.Message!.Contains("shared password"), Is.EqualTo(namesTheSharedPassword));
        }
    }

    /// <summary>
    /// An administrator's reset changed "Password never expires" on an account whose userAccountControl an export
    /// Synchronisation Rule with Enforce State writes: Enforce State will put the flow's value back, and with it whether
    /// a change is asked for at the next sign-in. Completed with a warning saying so, rather than leaving the helpdesk to
    /// find out from the user.
    /// </summary>
    [Test]
    public async Task Deliver_ExplicitRowChangingPasswordNeverExpires_UnderAnEnforcedFlow_CompletesWithAWarningAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);
        SeedAccountControlExportRule(enforceState: true);
        _connector.WithPasswordSetResult(_ => FlagChanged(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.Message, Does.Contain("Enforce State"));
            Assert.That(activity.Message, Does.Contain("Provisioning rule 9"), "names the Synchronisation Rule that puts it back");
        }
    }

    [TestCase(false, true, TestName = "Deliver_ExplicitRowChangingPasswordNeverExpires_FlowWithoutEnforceState_CompletesPlainlyAsync")]
    [TestCase(true, false, TestName = "Deliver_ExplicitRowNotChangingPasswordNeverExpires_UnderAnEnforcedFlow_CompletesPlainlyAsync")]
    public async Task Deliver_ExplicitRow_NothingWillPutTheFlagBack_CompletesPlainlyAsync(bool enforceState, bool flagChanged)
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);
        SeedAccountControlExportRule(enforceState);
        _connector.WithPasswordSetResult(_ => flagChanged
            ? FlagChanged(PasswordExpiryBehaviour.RequireChangeAtNextSignIn)
            : PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_createdActivities.Single().Status, Is.EqualTo(ActivityStatus.Complete));
    }

    /// <summary>
    /// The critique's missed case: an administrator resets an account Active Directory still holds disabled for its
    /// first password (#2039). The delivery completes the enable, and the held export still carries the flow's
    /// userAccountControl, which the next export writes back, "Password never expires" and all.
    /// </summary>
    [Test]
    public async Task Deliver_ExplicitRowChangingPasswordNeverExpires_OnAnAccountHeldForItsPassword_CompletesWithAWarningAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value);
        _connector.WithPasswordSetResult(_ => FlagChanged(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.Message, Does.Contain("held"));
        }
    }

    private static PasswordSetResult FlagChanged(PasswordExpiryBehaviour applied)
    {
        var result = PasswordSetResult.Succeeded(applied);
        result.PasswordNeverExpiresChanged = true;
        return result;
    }

    /// <summary>
    /// Seeds an export Synchronisation Rule on this Connected System whose Attribute Flow writes userAccountControl for
    /// the account's Object Type.
    /// </summary>
    private void SeedAccountControlExportRule(bool enforceState) =>
        _syncRepository.SeedSyncRule(new SyncRule
        {
            Id = 9,
            Name = "Provisioning rule 9",
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectTypeId = UserObjectTypeId,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = enforceState,
            AttributeFlowRules =
            [
                new SyncRuleMapping
                {
                    Id = 90,
                    TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 900, Name = "userAccountControl" },
                    TargetConnectedSystemAttributeId = 900
                }
            ]
        });

    #endregion

    #region an export to the account still in flight (#2173)

    /// <summary>
    /// The reported gap: a leaver's disabling export is being sent, and records that it wrote the enabled state only
    /// once its whole batch has come back. A delivery in that window would read the first password unmarked and enable
    /// the leaver on the rule's say-so. It waits instead: nothing is sent, nothing is counted against the change, and
    /// no failure is recorded, because nothing failed.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_ExportToTheAccountInFlight_WaitsWithoutSendingOrCountingAnAttemptAsync()
    {
        var now = DateTime.UtcNow;
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges[change.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty, "nothing is enabled on the rule's say-so while an export may be disabling the account");
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.Zero, "a wait is not an attempt");
            Assert.That(stored.NextRetryAt, Is.EqualTo(now + PasswordSynchronisationServer.ExportInFlightWait));
            Assert.That(stored.ClaimedBy, Is.Null);
            Assert.That(result.PostponedCount, Is.EqualTo(1), "the wait is counted, never silent");
            Assert.That(result.RetryingCount, Is.Zero);
            Assert.That(_createdActivities, Is.Empty, "nothing failed, so nothing is recorded as a failure");
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_ExportNoLongerInFlight_FollowsTheRuleAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value, PendingExportStatus.Exported);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
            Assert.That(result.PostponedCount, Is.Zero);
        }
    }

    /// <summary>
    /// A delivery that would leave the enabled state alone waits too (#2158), deliberately reversing #2173's narrower
    /// rule: an export in flight may be the enabling Update Active Directory refuses for want of a password, which the
    /// Connector then sends disabled and holds for this very delivery to complete. Delivered before that hold is
    /// recorded, the password would land without the enable, and the account would wait a schedule cycle for it.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_ExportInFlightAndTheFlowAlreadyDecides_WaitsWithoutSendingOrCountingAnAttemptAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(_syncRepository.PendingPasswordChanges[change.Id].AttemptCount, Is.Zero);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Enable Account off enables nothing, but where nothing has yet written the account's flags the rule's expiry still
    /// writes "Password never expires" (#2159). The export in flight may be the Attribute Flow that owns that flag, and
    /// records so only once it has finished, so JIM's own write of it waits as an enable does.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_ExportInFlightAndTheRuleOff_WaitsWithoutSendingOrCountingAnAttemptAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId, ip => ip.EnableAccount = false);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(_syncRepository.PendingPasswordChanges[change.Id].AttemptCount, Is.Zero);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A propagated password leaves the account's enabled state as it is, and waits all the same (#2158): it is as much
    /// the password an enable held for want of one is waiting for as a first password is.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRow_ExportInFlight_WaitsAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// An enable held on an Update (#2158), the account's first password already marked as having its enabled state set
    /// by the flow: the hold decides, so the delivery completes the enable rather than leaving it to the flow, and says so.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_EnableHeldOnAnUpdate_CompletesTheEnableAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value, changeType: PendingExportChangeType.Update);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
            Assert.That(result.WithheldEnablesCompletedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// An administrator who named the account and chose to enable it gets that, export or no export: the choice is
    /// theirs, and where the flow disagrees Enforce State puts the flow's value back.
    /// </summary>
    [Test]
    public async Task Deliver_ExplicitRowChoosingToEnable_ExportInFlight_DeliversAsync()
    {
        var change = await QueueExplicitAsync(enableAccount: true);
        ArrangeAccount(change);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
    }

    [Test]
    public async Task Deliver_ProvisionedRow_HeldEnableWithAnExportInFlight_WaitsAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        _syncRepository.PendingPasswordChanges[change.Id].EnabledStateSetByAttributeFlow = true;
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value, pendingExportStatus: PendingExportStatus.Executing);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Deliver_PropagatedRow_HeldEnableWithAnExportInFlight_WaitsAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value, pendingExportStatus: PendingExportStatus.Executing);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// The whole of the issue's scenario: the delivery waits while the leaver's export is being sent; the export
    /// finishes, marking the first password and leaving Executing; the next attempt leaves the enabled state to the
    /// flow rather than enabling the leaver.
    /// </summary>
    [Test]
    public async Task Deliver_PostponedRow_OnceTheExportHasFinished_LeavesTheEnabledStateToTheFlowAsync()
    {
        var now = DateTime.UtcNow;
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        var accountId = change.ConnectedSystemObjectId!.Value;
        var export = SeedExportInFlight(accountId);

        var whileInFlight = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);
        await _syncRepository.MarkOwedInitialPasswordsAsSetByAttributeFlowAsync(ConnectedSystemId, [accountId]);
        export.Status = PendingExportStatus.Exported;
        var afterwards = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now + PasswordSynchronisationServer.ExportInFlightWait, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileInFlight.PostponedCount, Is.EqualTo(1));
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.Null, "the leaver is not enabled");
            Assert.That(afterwards.EnabledStateLeftToAttributeFlowCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_ExplicitRowWithNoChoice_HeldEnableWithAnExportInFlight_WaitsAsync()
    {
        var change = await QueueExplicitAsync();
        ArrangeAccount(change);
        SeedHeldEnable(change.ConnectedSystemObjectId!.Value, pendingExportStatus: PendingExportStatus.Executing);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A wait is written back through the attempt write, which is guarded on the row still being the lane's: an
    /// administrator who cancels it meanwhile finds it cancelled, not rescheduled.
    /// </summary>
    [Test]
    public async Task Deliver_PostponedRowCancelledMidLane_KeepsTheCancellationAsync()
    {
        var now = DateTime.UtcNow;
        var (first, _) = await QueueProvisionedAsync(SyncRuleId, createdAt: now.AddMinutes(-2));
        var (second, _) = await QueueProvisionedAsync(SyncRuleId, createdAt: now.AddMinutes(-1));
        SeedExportInFlight(second.ConnectedSystemObjectId!.Value);
        _connector.WithPasswordSetResult(account =>
        {
            // Cancelled while the lane is busy with the account ahead of it.
            if (account.Id == first.ConnectedSystemObjectId)
                _syncRepository.PendingPasswordChanges[second.Id].Cancel(Guid.NewGuid(), "Ada Lovelace", DateTime.UtcNow);
            return PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn);
        });

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges[second.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
            Assert.That(stored.NextRetryAt, Is.Null);
        }
    }

    /// <summary>
    /// An export left in Executing by a run that failed or was cancelled stays there until the Worker restarts. A
    /// delivery that waited on it would wait until the change expired, so an export Executing for longer than any
    /// batch takes is treated as stranded rather than in flight, and the delivery goes ahead.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_ExportStrandedInExecuting_StopsWaitingForItAsync()
    {
        var now = DateTime.UtcNow;
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value,
            executingSince: now - PasswordSynchronisationServer.ExportInFlightCeiling - TimeSpan.FromMinutes(1));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
            Assert.That(result.PostponedCount, Is.Zero);
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_ExportExecutingForLessThanTheCeiling_StillWaitsAsync()
    {
        var now = DateTime.UtcNow;
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value,
            executingSince: now - PasswordSynchronisationServer.ExportInFlightCeiling + TimeSpan.FromMinutes(1));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        Assert.That(result.PostponedCount, Is.EqualTo(1));
    }

    /// <summary>
    /// However long the export runs, the wait never outlasts the change: it comes round again at its expiry at the
    /// latest, and is retired there rather than waiting on.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_ExportInFlightNearExpiry_WaitsNoLaterThanTheExpiryAsync()
    {
        var now = DateTime.UtcNow;
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        var expiresAt = now.AddSeconds(5);
        _syncRepository.PendingPasswordChanges[change.Id].ExpiresAt = expiresAt;
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        Assert.That(_syncRepository.PendingPasswordChanges[change.Id].NextRetryAt, Is.EqualTo(expiresAt));
    }

    /// <summary>
    /// Seeds a Pending Export for the account in the given status: being sent to the Connected System right now
    /// unless told otherwise, marked Executing at <paramref name="executingSince"/> (now unless told otherwise).
    /// </summary>
    private PendingExport SeedExportInFlight(Guid accountId, PendingExportStatus status = PendingExportStatus.Executing,
        DateTime? executingSince = null)
    {
        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = accountId,
            ChangeType = PendingExportChangeType.Update,
            Status = status,
            LastAttemptedAt = executingSince ?? DateTime.UtcNow,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    IntValue = 514,
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            ]
        };
        _syncRepository.SeedPendingExport(export);
        return export;
    }

    #endregion

    /// <summary>
    /// Seeds a Pending Export for the account whose <c>userAccountControl</c> change was written with an interim
    /// disabled value until the password is delivered (#2039): still awaiting confirmation unless told otherwise.
    /// </summary>
    private void SeedHeldEnable(Guid accountId, PendingExportAttributeChangeStatus status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
        PendingExportStatus pendingExportStatus = PendingExportStatus.Exported, PendingExportChangeType changeType = PendingExportChangeType.Create) =>
        _syncRepository.SeedPendingExport(new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = accountId,
            ChangeType = changeType,
            Status = pendingExportStatus,
            LastAttemptedAt = DateTime.UtcNow,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Add,
                    IntValue = 512,
                    AwaitsInitialPassword = status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    Status = status
                }
            ]
        });

    #endregion

    [Test]
    public async Task Deliver_ProvisionedRow_OnADisabledSystem_IsDeliveredAsync()
    {
        // Decision D1, widened by #1697: the account is already named, so there is no configuration
        // decision left to defer to, exactly like an explicit set.
        _configuration.Enabled = false;
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_connector.PasswordSetAttempts.Single().ConnectedSystemObjectId, Is.EqualTo(change.ConnectedSystemObjectId));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_OnAnUnconfiguredSystem_IsDeliveredUnderTheDefaultPolicyAsync()
    {
        _connectedSystem.PasswordSynchronisation = null;
        await QueueProvisionedAsync(SyncRuleId);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_connector.PasswordSetAttempts.Single().PasswordLength, Is.GreaterThan(0));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_WhoseAccountIsGone_IsWithdrawnNotParkedAsync()
    {
        await QueueProvisionedAsync(SyncRuleId, arrangeAccount: false);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WithdrawnCount, Is.EqualTo(1));
            Assert.That(result.ParkedCount, Is.Zero);
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete), "Nothing went wrong; the work simply stopped being needed.");
            Assert.That(activity.Message, Does.Contain("no longer needed").And.Contain("Corporate AD"));
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_WhoseRuleNoLongerSetsAPassword_IsWithdrawnAsync()
    {
        await QueueProvisionedAsync(SyncRuleId, ip => ip.Enabled = false);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WithdrawnCount, Is.EqualTo(1));
            Assert.That(result.ParkedCount, Is.Zero);
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.Message, Does.Contain("no longer sets one"));
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_WhoseRuleHasGone_IsWithdrawnAsync()
    {
        // No SyncRule is ever seeded for this id: the export that provisioned the account named a rule that has
        // since been deleted.
        var now = DateTime.UtcNow;
        var accountId = Guid.NewGuid();
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = accountId,
            EncryptedPassword = null,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            SyncRuleId = 999999,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            ActivityId = Guid.NewGuid()
        };
        await _syncRepository.QueuePasswordChangesAsync([change]);
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId))
            .ReturnsAsync([new ConnectedSystemObject { Id = accountId, ConnectedSystemId = ConnectedSystemId, TypeId = UserObjectTypeId }]);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WithdrawnCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.Message, Does.Contain("no longer sets one").And.Contain("has been deleted"));
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_StaticPasswordMissing_ParksAsConfigurationFaultAsync()
    {
        await QueueProvisionedAsync(SyncRuleId, ip =>
        {
            ip.Source = InitialPasswordSource.Static;
            ip.StaticPasswordEncryptedValue = null;
        });

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(_connector.PasswordSetAttempts, Is.Empty, "The Connector must never be asked to set nothing.");
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_UnusablePolicy_ParksAsConfigurationFaultAsync()
    {
        await QueueProvisionedAsync(SyncRuleId, ip =>
        {
            ip.Source = InitialPasswordSource.Custom;
            ip.CustomPolicy.Length = 8;
            ip.CustomPolicy.MinimumUppercase = 100;
        });

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_PolicyRejection_ParksAsync()
    {
        await QueueProvisionedAsync(SyncRuleId);
        _connector.WithPasswordSetResult(_ =>
            PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Password does not meet complexity requirements"));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(stored.TargetMessage, Is.EqualTo("Password does not meet complexity requirements"));
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_TransientFailure_SchedulesARetryAsync()
    {
        await QueueProvisionedAsync(SyncRuleId);
        _connector.WithPasswordSetResult(_ =>
            PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Server unavailable"));

        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryingCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
            Assert.That(stored.NextRetryAt, Is.EqualTo(now.AddMinutes(5)));
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_RetriesExhausted_ParksAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);
        change.AttemptCount = 3;
        await _syncRepository.RecordPasswordChangeAttemptsAsync([change]);
        _connector.WithPasswordSetResult(_ =>
            PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Still unavailable"));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges.Values.Single().Status,
                Is.EqualTo(PendingPasswordChangeStatus.Parked));
        }
    }

    /// <summary>
    /// A Provisioned row carries no password of its own: every attempt resolves it fresh from the Synchronisation
    /// Rule's current settings, so a configuration change between two failed attempts takes effect on the very
    /// next one rather than waiting for the row to be re-queued.
    /// </summary>
    [Test]
    public async Task Deliver_ProvisionedRow_ReadsTheRuleAgainOnEachAttemptAsync()
    {
        var (_, rule) = await QueueProvisionedAsync(SyncRuleId, ip => ip.ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn);
        _connector.WithPasswordSetResult(_ => PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Server unavailable"));

        var firstAttempt = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        await _server.DeliverDuePasswordChangesAsync(_connectedSystem, _connector, ClaimedBy, firstAttempt, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts, Has.Count.EqualTo(1));
        Assert.That(_connector.PasswordSetAttempts[0].Options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));

        // The administrator changes the rule's settings between the two lanes.
        rule.InitialPassword!.ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires;
        _connector.WithPasswordSetResult(_ => PasswordSetResult.Succeeded(PasswordExpiryBehaviour.NeverExpires));

        var secondAttempt = firstAttempt.AddHours(1);
        var result = await _server.DeliverDuePasswordChangesAsync(_connectedSystem, _connector, ClaimedBy, secondAttempt, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_connector.PasswordSetAttempts, Has.Count.EqualTo(2));
            Assert.That(_connector.PasswordSetAttempts[1].Options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires),
                "The second attempt must read the rule's settings again rather than reuse what the first attempt saw.");
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_RecordsAChildActivityUnderTheStagedParentAsync()
    {
        var (change, _) = await QueueProvisionedAsync(SyncRuleId);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ParentActivityId, Is.EqualTo(change.ActivityId));
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.Message, Does.Contain("Initial password set"));
        }
    }

    [Test]
    public async Task Deliver_ProvisionedRow_NeverRecordsThePasswordAsync()
    {
        const string staticPassword = "Correct-Horse-Provisioned-99";
        await QueueProvisionedAsync(SyncRuleId, ip =>
        {
            ip.Source = InitialPasswordSource.Static;
            ip.StaticPasswordEncryptedValue = _protection.Protect(staticPassword);
        });

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var activity = _createdActivities.Single();
        var text = $"{activity.TargetName} {activity.Message} {activity.TargetContext}";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Not.Contain("Correct-Horse").And.Not.Contain("Provisioned-99"));
            Assert.That(text, Does.Not.Contain(_protection.Protect(staticPassword)!), "Not the ciphertext either.");
        }
    }

    #endregion

    #region a password change carrying an account's first password (#2160)

    /// <summary>
    /// The person's own password change, staged as a new account's first password in place of its Initial Password:
    /// the rule's Initial Password settings, and the account the obligation is for, as staging records them.
    /// </summary>
    private async Task<PendingPasswordChange> QueueCarryingAFirstPasswordAsync(
        Action<SyncRuleInitialPassword>? configure = null,
        PendingPasswordChangeOrigin origin = PendingPasswordChangeOrigin.Propagated,
        bool? enableAccount = null,
        string password = "the-persons-own-password",
        Guid? owedAccountId = null,
        DateTime? createdAt = null)
    {
        var initialPassword = new SyncRuleInitialPassword
        {
            SyncRuleId = SyncRuleId,
            Enabled = true,
            Source = InitialPasswordSource.Discovered,
            ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
            EnableAccount = true
        };
        configure?.Invoke(initialPassword);
        _syncRepository.SeedSyncRule(new SyncRule { Id = SyncRuleId, Name = "Provisioning rule", InitialPassword = initialPassword });
        _syncRepository.SeedConnectedSystem(_connectedSystem);

        var change = origin == PendingPasswordChangeOrigin.Explicit
            ? await QueueExplicitAsync(enableAccount, createdAt)
            : await QueueAsync(password, createdAt: createdAt);
        ArrangeAccount(change);

        var stored = _syncRepository.PendingPasswordChanges[change.Id];
        stored.ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy;
        stored.CarryInitialPassword(owedAccountId ?? change.ConnectedSystemObjectId!.Value, SyncRuleId, accountFlagsWrittenByExport: false,
            stored.ExpiresAt);
        return stored;
    }

    /// <summary>
    /// The reported bug: the person's password change was waiting when JIM provisioned the account, and was delivered
    /// as an ordinary propagated password, so nothing enabled the account and it was left disabled with nothing to say
    /// why. Carrying the first password, it is enabled as the rule's Initial Password asks, and the Activity says so.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_RuleOn_EnablesAndSaysSoAsync()
    {
        await QueueCarryingAFirstPasswordAsync();

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var outcome = _createdActivities.Single(a => a.TargetType == ActivityTargetType.PasswordSynchronisation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(result.FirstPasswordsDeliveredInPlaceCount, Is.EqualTo(1));
            Assert.That(outcome.Message, Does.Contain("first password"));
            Assert.That(outcome.Message, Does.Contain("enabled as the Synchronisation Rule's Initial Password asks"));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    /// <summary>
    /// Passwords replace each other: the person's own password is sent, never the rule's generated one, and with the
    /// person's own expiry. The rule's "Require a change" belongs to the Initial Password, which is not delivered; only
    /// its promise about "Password never expires" (here, cleared) carries forward.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_SendsThePersonsPasswordAndExpiryAndClearsNeverExpiresAsync()
    {
        await QueueCarryingAFirstPasswordAsync(password: "the-persons-own-password");

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var attempt = _connector.PasswordSetAttempts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempt.PasswordLength, Is.EqualTo("the-persons-own-password".Length));
            Assert.That(attempt.Options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy),
                "never the rule's Require a change on a password the person chose");
            Assert.That(attempt.Options.LeavePasswordNeverExpiresAsIs, Is.False, "the rule's promise decides the flag: cleared");
        }
    }

    /// <summary>
    /// The rule promises "Password never expires": the flag is set with the person's password, which therefore never
    /// expires, as an Initial Password of Never expires would have.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_RuleNeverExpires_SetsPasswordNeverExpiresAsync()
    {
        await QueueCarryingAFirstPasswordAsync(ip => ip.ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var options = _connector.PasswordSetAttempts.Single().Options;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(options.LeavePasswordNeverExpiresAsIs, Is.False);
        }
    }

    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_RuleOff_LeavesItAsCreatedAndSaysSoAsync()
    {
        await QueueCarryingAFirstPasswordAsync(ip => ip.EnableAccount = false);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var outcome = _createdActivities.Single(a => a.TargetType == ActivityTargetType.PasswordSynchronisation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.Null);
            Assert.That(outcome.Message, Does.Contain("left as it was created"));
        }
    }

    /// <summary>
    /// The Create wrote the account's flags (an Attribute Flow sets userAccountControl), so the flow decides both, as it
    /// would for the Initial Password: a pre-hire stays disabled.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_FlagsWrittenByAnExport_LeavesBothToTheFlowAsync()
    {
        var change = await QueueCarryingAFirstPasswordAsync();
        change.EnabledStateSetByAttributeFlow = true;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var options = _connector.PasswordSetAttempts.Single().Options;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.EnableAccount, Is.Null);
            Assert.That(options.LeavePasswordNeverExpiresAsIs, Is.True);
            Assert.That(result.EnabledStateLeftToAttributeFlowCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A leaver disabled after the lane claimed the change: the mark is read afresh at the attempt, so the delivery
    /// leaves the enabled state to the flow rather than re-enabling the leaver on the rule's say-so.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_MarkedAfterTheClaim_LeavesItToTheFlowAsync()
    {
        var now = DateTime.UtcNow;
        var first = await QueueCarryingAFirstPasswordAsync(createdAt: now.AddMinutes(-2));
        var second = await QueueCarryingAFirstPasswordAsync(createdAt: now.AddMinutes(-1));
        _connector.WithPasswordSetResult(account =>
        {
            // The leaver's export lands while the lane is busy with the first account.
            if (account.Id == first.ConnectedSystemObjectId)
                _syncRepository.PendingPasswordChanges[second.Id].EnabledStateSetByAttributeFlow = true;
            return PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy);
        });

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        var attempts = _connector.PasswordSetAttempts.ToDictionary(a => a.ConnectedSystemObjectId, a => a.Options.EnableAccount);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts[first.ConnectedSystemObjectId!.Value], Is.True, "nothing set the first account's enabled state");
            Assert.That(attempts[second.ConnectedSystemObjectId!.Value], Is.Null, "marked after the claim, before its attempt");
        }
    }

    /// <summary>
    /// JIM's own enable, on the rule's say-so, waits for an export to the account in flight (#2173), exactly as the
    /// Initial Password's would.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_ExportToTheAccountInFlight_WaitsWithoutCountingAnAttemptAsync()
    {
        var change = await QueueCarryingAFirstPasswordAsync();
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges[change.Id].AttemptCount, Is.Zero);
        }
    }

    /// <summary>
    /// The rule no longer sets an Initial Password: the change is still the person's own password, so it is delivered,
    /// never withdrawn as an Initial Password would be; the account's flags are left, and the Activity says why.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_RuleNoLongerSetsOne_DeliversWithoutEnablingAndSaysSoAsync()
    {
        await QueueCarryingAFirstPasswordAsync(ip => ip.Enabled = false);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var outcome = _createdActivities.Single(a => a.TargetType == ActivityTargetType.PasswordSynchronisation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(result.WithdrawnCount, Is.Zero);
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.Null);
            Assert.That(_connector.PasswordSetAttempts.Single().Options.LeavePasswordNeverExpiresAsIs, Is.True);
            Assert.That(outcome.Message, Does.Contain("no longer sets an Initial Password"));
        }
    }

    /// <summary>
    /// The change reached another account than the one it owes its first password (that one was disjoined from the
    /// person, and another of theirs joined without JIM provisioning it): the promises are never applied to an account
    /// they were not made for, and the run counts them as not applied.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_ResolvesToAnotherAccount_DoesNotEnableItAsync()
    {
        await QueueCarryingAFirstPasswordAsync(owedAccountId: Guid.NewGuid());

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.Null);
            Assert.That(result.FirstPasswordPromisesNotAppliedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A change carrying an account's first password is delivered like the Initial Password it stands in for, whatever
    /// the system's Password Synchronisation says (decision D1): the pause cannot strand a new account without a
    /// password, or let the obligation expire unseen.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_OnAPausedSystem_IsDeliveredAsync()
    {
        await QueueCarryingAFirstPasswordAsync();
        _configuration.Enabled = false;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(result.DeliveredCount, Is.EqualTo(1));
    }

    /// <summary>
    /// Behaviour change (#2160): an administrator's set with no enable choice, on an account still owed its first
    /// password, follows the rule's Enable Account, as it already completes a withheld enable.
    /// </summary>
    [Test]
    public async Task Deliver_ExplicitRowWithNoChoiceCarryingAFirstPassword_FollowsTheRulesEnableAsync()
    {
        await QueueCarryingAFirstPasswordAsync(origin: PendingPasswordChangeOrigin.Explicit);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connector.PasswordSetAttempts.Single().Options.EnableAccount, Is.True);
    }

    /// <summary>
    /// The Connector set the password but could not enable the account (a directory with no such operation reports it
    /// as a warning): the Activity must not claim the account was enabled.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRowCarryingAFirstPassword_ConnectorCouldNotEnable_DoesNotClaimItWasEnabledAsync()
    {
        await QueueCarryingAFirstPasswordAsync();
        _connector.WithPasswordSetResult(_ => PasswordSetResult.SucceededWithExpiryDowngrade(
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
            "Enabling the account is an Active Directory operation, so the account's enabled state was left unchanged."));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var outcome = _createdActivities.Single(a => a.TargetType == ActivityTargetType.PasswordSynchronisation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(outcome.Message, Does.Not.Contain("enabled as the Synchronisation Rule's Initial Password asks"));
            Assert.That(outcome.Message, Does.Contain("left unchanged"));
        }
    }

    /// <summary>
    /// The account is being created right now (#2160): its entry may already exist part-way through the Create's batch,
    /// and the export stages its Initial Password only once the batch has come back. A change owing no first password
    /// delivered and removed in between would let that Initial Password be queued over the person's password, so it
    /// waits, counting nothing; staging then gives it the account's first password.
    /// </summary>
    [Test]
    public async Task Deliver_PropagatedRow_CreateOfTheAccountInFlight_WaitsWithoutCountingAnAttemptAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value).ChangeType = PendingExportChangeType.Create;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges[change.Id].AttemptCount, Is.Zero);
        }
    }

    /// <summary>
    /// The same holds for an administrator's set with a choice, which otherwise never waits for an export.
    /// </summary>
    [Test]
    public async Task Deliver_ExplicitRowChoosingToEnable_CreateOfTheAccountInFlight_WaitsAsync()
    {
        var change = await QueueExplicitAsync(enableAccount: true);
        ArrangeAccount(change);
        SeedExportInFlight(change.ConnectedSystemObjectId!.Value).ChangeType = PendingExportChangeType.Create;

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(result.PostponedCount, Is.EqualTo(1));
        }
    }

    #endregion

    #region claims (#1635)

    /// <summary>
    /// The lane works over rows it claims. While a change is being attempted its row is Delivering under the
    /// lane's instance id, which is what stops a second deliverer taking it and what a caller waiting on the
    /// change is shown.
    /// </summary>
    [Test]
    public async Task Deliver_HoldsTheClaimWhileAttemptingAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        PendingPasswordChangeStatus? statusDuringAttempt = null;
        string? claimantDuringAttempt = null;
        _connector.WithPasswordSetResult(_ =>
        {
            var stored = _syncRepository.PendingPasswordChanges[change.Id];
            statusDuringAttempt = stored.Status;
            claimantDuringAttempt = stored.ClaimedBy;
            return PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy);
        });

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusDuringAttempt, Is.EqualTo(PendingPasswordChangeStatus.Delivering));
            Assert.That(claimantDuringAttempt, Is.EqualTo(ClaimedBy));
        }
    }

    [Test]
    public async Task Deliver_AFailedAttempt_EndsTheClaimAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ => PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Server unavailable"));

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.ClaimedAt, Is.Null);
            Assert.That(stored.ClaimedBy, Is.Null);
        }
    }

    /// <summary>
    /// Decision D9 (#1697): a delete guarded only by id would remove a row a newer password has since
    /// superseded. The lane's own claimed copy is detached from the store (documented on
    /// <see cref="JIM.InMemoryData.SyncRepository.ClaimDuePasswordChangesAsync"/>), so a supersession landing on
    /// the stored row mid-attempt is invisible to the lane; only the delete guard on the stored row's status
    /// stops the older delivery's success from removing the newer work.
    /// </summary>
    [Test]
    public async Task Deliver_RowSupersededMidFlight_IsNotDeletedOnSuccessAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        var newerActivityId = Guid.NewGuid();
        _connector.WithPasswordSetResult(_ =>
        {
            // A newer password arrived and coalesced onto this row while the attempt was in flight, exactly as
            // QueuePasswordChangesAsync's coalescing would: the row is Pending again with newer work by the time
            // the delivery succeeds.
            var newer = new PendingPasswordChange
            {
                EncryptedPassword = _protection.ProtectPassword("a-newer-password")!,
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                Origin = PendingPasswordChangeOrigin.Propagated,
                ConnectedSystemObjectId = change.ConnectedSystemObjectId,
                ActivityId = newerActivityId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            };
            _syncRepository.PendingPasswordChanges[change.Id].Supersede(newer);
            return PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy);
        });

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1), "The lane still reports the delivery it made.");
            Assert.That(stored.Id, Is.EqualTo(change.Id), "The superseding change replaced this row in place rather than the row being deleted under it.");
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending), "The newer change survives the older delivery's success.");
            Assert.That(stored.ActivityId, Is.EqualTo(newerActivityId));
        }
    }

    [Test]
    public async Task Deliver_WithAConnectorThatCannotSetPasswords_ReleasesTheClaimUnattemptedAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, new MockFileConnector(), ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending), "Given back exactly as it was, still due.");
            Assert.That(stored.ClaimedBy, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
        }
    }

    [Test]
    public async Task Deliver_SecureTransportRefused_ReleasesTheClaimUnattemptedAsync()
    {
        _connectedSystem.RequireSecureTransport = true;
        _connector.PasswordChannelSecure = false;
        var change = await QueueAsync();
        ArrangeAccount(change);

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PasswordChannelNotSecure, Is.True);
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.ClaimedBy, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
        }
    }

    [Test]
    public async Task Deliver_CancelledMidBatch_ReleasesTheChangesNotReachedAsync()
    {
        // The first change lands, cancellation arrives, and the second is given back unattempted rather than left
        // claimed for the whole lease or counted as tried.
        var now = DateTime.UtcNow;
        var first = await QueueAsync(createdAt: now.AddMinutes(-2));
        var second = await QueueAsync(createdAt: now.AddMinutes(-1));
        ArrangeAccount(first);
        ArrangeAccount(second);
        using var cancellation = new CancellationTokenSource();
        _connector.WithPasswordSetResult(_ =>
        {
            cancellation.Cancel();
            return PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy);
        });

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, cancellation.Token);

        var remaining = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(result.ReleasedCount, Is.EqualTo(1));
            Assert.That(remaining.Id, Is.EqualTo(second.Id), "The delivered change is gone; the other is what remains.");
            Assert.That(remaining.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(remaining.ClaimedBy, Is.Null);
            Assert.That(remaining.AttemptCount, Is.Zero);
        }
    }

    /// <summary>
    /// An administrator who cancels a change while the directory is being written to must find it cancelled
    /// afterwards. The attempt's outcome is only recorded against a row still in the lane's hands.
    /// </summary>
    [Test]
    public async Task Deliver_RowCancelledMidFlight_KeepsTheCancellationAsync()
    {
        var change = await QueueAsync();
        ArrangeAccount(change);
        _connector.WithPasswordSetResult(_ =>
        {
            _syncRepository.PendingPasswordChanges[change.Id].Cancel(Guid.NewGuid(), "Ada Lovelace", DateTime.UtcNow);
            return PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Server unavailable");
        });

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
            Assert.That(stored.AttemptCount, Is.Zero, "The attempt landed nowhere; the cancellation stands.");
        }
    }

    /// <summary>
    /// A row reclaimed from a deliverer that died holding it can be past its window, because expiry never touches
    /// a Delivering row. The lane retires it rather than sending a password whose time has gone.
    /// </summary>
    [Test]
    public async Task Deliver_ReclaimedChangePastItsWindow_IsExpiredNotAttemptedAsync()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = await QueueAsync(createdAt: now.AddDays(-8));
        ArrangeAccount(change);
        var stored = _syncRepository.PendingPasswordChanges[change.Id];
        stored.Claim("worker-that-died", now - PasswordSynchronisationServer.ClaimLease - TimeSpan.FromMinutes(1));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExpiredCount, Is.EqualTo(1));
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
            Assert.That(stored.ClaimedBy, Is.Null);
        }
    }

    [Test]
    public async Task Deliver_ChangeClaimedByALiveDeliverer_IsLeftToItAsync()
    {
        var now = DateTime.UtcNow;
        var change = await QueueAsync();
        ArrangeAccount(change);
        _syncRepository.PendingPasswordChanges[change.Id].Claim("another-worker", now.AddSeconds(-5));

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, now, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasSomethingToReport, Is.False);
            Assert.That(_connector.PasswordSetAttempts, Is.Empty);
            Assert.That(_syncRepository.PendingPasswordChanges[change.Id].ClaimedBy, Is.EqualTo("another-worker"));
        }
    }

    [Test]
    public async Task Deliver_MoreChangesThanOneClaim_ClaimsAgainUntilDrainedAsync()
    {
        // Claims are small so a claim is never held for much longer than the attempts it covers; a lane keeps
        // claiming until the system's queue is drained or the pass bound is reached.
        var accountId = Guid.NewGuid();
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync([new ConnectedSystemObject { Id = accountId, ConnectedSystemId = ConnectedSystemId, TypeId = UserObjectTypeId }]);
        var total = PasswordSynchronisationServer.ClaimBatchSize + PasswordSynchronisationServer.ClaimBatchSize / 2;
        for (var i = 0; i < total; i++)
            await QueueAsync();

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(total));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(_connector.PasswordConnectionOpen, Is.False, "One channel for the whole lane, closed at the end.");
        }
    }

    [Test]
    public async Task Deliver_StopsAtThePassBoundAndLeavesTheRestDueAsync()
    {
        var accountId = Guid.NewGuid();
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync([new ConnectedSystemObject { Id = accountId, ConnectedSystemId = ConnectedSystemId, TypeId = UserObjectTypeId }]);
        var total = PasswordSynchronisationServer.MaximumChangesPerPass + 1;
        for (var i = 0; i < total; i++)
            await QueueAsync();

        var result = await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var leftOver = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(PasswordSynchronisationServer.MaximumChangesPerPass));
            Assert.That(leftOver.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending), "Left due for the next lane, not claimed.");
            Assert.That(leftOver.ClaimedBy, Is.Null);
        }
    }

    #endregion
}
