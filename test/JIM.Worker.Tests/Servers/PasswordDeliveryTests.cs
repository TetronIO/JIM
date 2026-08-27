// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
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
            _ => Task.CompletedTask);
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

    private void ArrangeAccount(PendingPasswordChange change) =>
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId))
            .ReturnsAsync([
                new ConnectedSystemObject
                {
                    Id = change.ConnectedSystemObjectId ?? Guid.NewGuid(),
                    ConnectedSystemId = ConnectedSystemId,
                    TypeId = UserObjectTypeId
                }
            ]);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, now, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, now, CancellationToken.None);

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
            _connectedSystem, new MockFileConnector(), DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_createdActivities.Single().Status, Is.EqualTo(ActivityStatus.Complete));
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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_createdActivities.Single().ErrorMessage ?? string.Empty,
            Does.Not.Contain("Correct-Horse").And.Not.Contain(change.EncryptedPassword));
    }

    [Test]
    public async Task Deliver_NeverRecordsThePasswordOnAnActivityAsync()
    {
        var change = await QueueAsync("Correct-Horse-Battery-Staple");
        ArrangeAccount(change);

        await _server.DeliverDuePasswordChangesAsync(
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

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
            _connectedSystem, _connector, now, CancellationToken.None);

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
            _connectedSystem, _connector, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingPasswordChanges.Values.Single().Status,
                Is.EqualTo(PendingPasswordChangeStatus.Parked));
        }
    }
}
