// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The Password Synchronisation queue row (#1119): one password change owed to one Connected System.
/// <para>
/// It carries the password itself, encrypted at rest, so a newer change for the same target must replace an
/// older one rather than queue behind it, and delivery is scheduled on a clock of its own rather than riding an
/// export run.
/// </para>
/// </summary>
[TestFixture]
public class PendingPasswordChangeTests
{
    private static PendingPasswordChange Change() => new()
    {
        MetaverseObjectId = Guid.NewGuid(),
        ConnectedSystemId = 3,
        EncryptedPassword = "$JIMPW$v1$ciphertext",
        ActivityId = Guid.NewGuid()
    };

    /// <summary>
    /// A newer change for the same identity and Connected System, as fan-out would build it: what
    /// <see cref="PendingPasswordChange.Supersede"/> takes its replacement values from.
    /// </summary>
    private static PendingPasswordChange Newer(
        string? encryptedPassword,
        PasswordExpiryBehaviour expiryBehaviour,
        Guid activityId,
        TimeSpan timeToLive,
        DateTime createdAt,
        PendingPasswordChangeOrigin origin = PendingPasswordChangeOrigin.Propagated,
        bool? enableAccount = null,
        int? syncRuleId = null) => new()
    {
        EncryptedPassword = encryptedPassword,
        ExpiryBehaviour = expiryBehaviour,
        ActivityId = activityId,
        CreatedAt = createdAt,
        ExpiresAt = createdAt + timeToLive,
        Origin = origin,
        EnableAccount = enableAccount,
        SyncRuleId = syncRuleId
    };

    /// <summary>
    /// An Initial Password as export staging writes it (#1697, #2160): no password of its own, the provisioning rule,
    /// and the account it is the first password for.
    /// </summary>
    private static PendingPasswordChange Provisioned(Guid accountId, int syncRuleId, bool accountFlagsWrittenByExport, DateTime createdAt) => new()
    {
        MetaverseObjectId = Guid.NewGuid(),
        ConnectedSystemId = 3,
        ConnectedSystemObjectId = accountId,
        ProvisionedConnectedSystemObjectId = accountId,
        Origin = PendingPasswordChangeOrigin.Provisioned,
        SyncRuleId = syncRuleId,
        EnabledStateSetByAttributeFlow = accountFlagsWrittenByExport,
        ActivityId = Guid.NewGuid(),
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddDays(7)
    };

    [Test]
    public void NewChange_StartsPendingWithNoAttempts()
    {
        var change = Change();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.AttemptCount, Is.Zero);
            Assert.That(change.LastAttemptedAt, Is.Null);
            Assert.That(change.NextRetryAt, Is.Null,
                "A change nobody has attempted is due immediately, not at some scheduled time.");
        }
    }

    [Test]
    public void IsDue_WithNoRetryScheduled_IsTrue()
    {
        // A first attempt, and a change released by enabling the system, both arrive with no schedule and must
        // be picked up by the next delivery pass.
        var change = Change();

        Assert.That(change.IsDue(DateTime.UtcNow), Is.True);
    }

    [Test]
    public void IsDue_BeforeTheScheduledRetry_IsFalse()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.NextRetryAt = now.AddMinutes(5);

        Assert.That(change.IsDue(now), Is.False);
    }

    [Test]
    public void IsDue_AtOrAfterTheScheduledRetry_IsTrue()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.NextRetryAt = now;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.IsDue(now), Is.True, "The retry falls due at its scheduled instant, not after it.");
            Assert.That(change.IsDue(now.AddSeconds(1)), Is.True);
        }
    }

    [Test]
    public void IsDue_WhenNotPending_IsFalse()
    {
        // Parked and Expired are terminal for the delivery pass: only a person, or a newer change, moves them.
        var now = DateTime.UtcNow;

        var parked = Change();
        parked.Status = PendingPasswordChangeStatus.Parked;

        var expired = Change();
        expired.Status = PendingPasswordChangeStatus.Expired;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parked.IsDue(now), Is.False);
            Assert.That(expired.IsDue(now), Is.False);
        }
    }

    [Test]
    public void HasExpired_AtOrAfterTheExpiry_IsTrue()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.ExpiresAt = now;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.HasExpired(now), Is.True);
            Assert.That(change.HasExpired(now.AddTicks(-1)), Is.False);
        }
    }

    [Test]
    public void HasExpired_WhenAlreadyTerminal_IsFalse()
    {
        // An expired row stays expired rather than being re-expired on every pass, and a parked one is waiting
        // on a person rather than on the clock.
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        var expired = Change();
        expired.ExpiresAt = now.AddDays(-1);
        expired.Status = PendingPasswordChangeStatus.Expired;

        var parked = Change();
        parked.ExpiresAt = now.AddDays(-1);
        parked.Status = PendingPasswordChangeStatus.Parked;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expired.HasExpired(now), Is.False);
            Assert.That(parked.HasExpired(now), Is.False,
                "A parked change is an administrator's to resolve; expiring it under them would remove the thing " +
                "they were asked to look at.");
        }
    }

    [Test]
    public void RecordAttempt_WithATransientFailure_SchedulesTheNextRetry()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var configuration = new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = 3, MaxRetries = 5, RetryBackoffBase = TimeSpan.FromMinutes(5)
        };

        var change = Change();
        change.ExpiresAt = now.AddDays(7);
        change.RecordAttempt(PasswordSetFailureReason.Transient, "Server unavailable", configuration, now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.AttemptCount, Is.EqualTo(1));
            Assert.That(change.LastAttemptedAt, Is.EqualTo(now));
            Assert.That(change.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
            Assert.That(change.TargetMessage, Is.EqualTo("Server unavailable"));
            Assert.That(change.NextRetryAt, Is.EqualTo(now.AddMinutes(5)));
        }
    }

    [Test]
    public void RecordAttempt_WithAPolicyRejection_ParksImmediately()
    {
        // Requirement 13: the password came from the person, so JIM has nothing else to send. Retrying would
        // present the same password and collect the same refusal.
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var configuration = new ConnectedSystemPasswordSynchronisation { ConnectedSystemId = 3 };

        var change = Change();
        change.ExpiresAt = now.AddDays(7);
        change.RecordAttempt(PasswordSetFailureReason.PolicyRejection, "Password too short", configuration, now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(change.NextRetryAt, Is.Null, "A parked change waits on a person, not on the clock.");
            Assert.That(change.TargetMessage, Is.EqualTo("Password too short"));
        }
    }

    [Test]
    public void RecordAttempt_WithAnUnsupportedOperation_ParksImmediately()
    {
        var now = DateTime.UtcNow;
        var configuration = new ConnectedSystemPasswordSynchronisation { ConnectedSystemId = 3 };

        var change = Change();
        change.ExpiresAt = now.AddDays(7);
        change.RecordAttempt(PasswordSetFailureReason.UnsupportedOperation, "Not supported", configuration, now);

        Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
    }

    [Test]
    public void RecordAttempt_WhenRetriesAreExhausted_Parks()
    {
        // Exhausting retries is still a state a person can act on: they fix the cause and retry from the queue.
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var configuration = new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = 3, MaxRetries = 2, RetryBackoffBase = TimeSpan.FromMinutes(5)
        };

        var change = Change();
        change.ExpiresAt = now.AddDays(7);
        change.AttemptCount = 2;

        change.RecordAttempt(PasswordSetFailureReason.Transient, "Still unavailable", configuration, now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.AttemptCount, Is.EqualTo(3));
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(change.NextRetryAt, Is.Null);
        }
    }

    [Test]
    public void RecordAttempt_NeverSchedulesARetryPastTheExpiry()
    {
        // A retry booked beyond the row's own expiry would come round to find nothing to attempt.
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var configuration = new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = 3, MaxRetries = 20, RetryBackoffBase = TimeSpan.FromHours(12)
        };

        var change = Change();
        change.ExpiresAt = now.AddHours(1);
        change.AttemptCount = 4;

        change.RecordAttempt(PasswordSetFailureReason.Transient, null, configuration, now);

        Assert.That(change.NextRetryAt, Is.LessThanOrEqualTo(change.ExpiresAt));
    }

    [Test]
    public void Supersede_ReplacesTheValueAndResetsTheAttemptHistory()
    {
        // Coalescing: the row holds the latest intended password for this target, not a queue of past ones.
        // The attempt history belongs to the password that has just been replaced, so it goes with it.
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var newActivityId = Guid.NewGuid();

        var change = Change();
        change.Status = PendingPasswordChangeStatus.Parked;
        change.AttemptCount = 4;
        change.FailureReason = PasswordSetFailureReason.PolicyRejection;
        change.TargetMessage = "Password too short";
        change.NextRetryAt = now.AddHours(3);

        change.Supersede(Newer("$JIMPW$v1$newer", PasswordExpiryBehaviour.NeverExpires, newActivityId,
            TimeSpan.FromDays(7), now));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.EncryptedPassword, Is.EqualTo("$JIMPW$v1$newer"));
            Assert.That(change.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(change.ActivityId, Is.EqualTo(newActivityId),
                "The row points at the Activity for the change it is now carrying.");
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending),
                "A newer password is worth trying even where the previous one was parked or expired.");
            Assert.That(change.AttemptCount, Is.Zero);
            Assert.That(change.FailureReason, Is.Null);
            Assert.That(change.TargetMessage, Is.Null);
            Assert.That(change.NextRetryAt, Is.Null);
            Assert.That(change.ExpiresAt, Is.EqualTo(now.AddDays(7)),
                "The expiry runs from the new change, not from the one it replaced.");
        }
    }

    [Test]
    public void Retry_ClearsTheFailureAndMakesTheChangeDueImmediately()
    {
        // The manual retry from the queue page: an administrator has fixed the cause and wants it tried now.
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        var change = Change();
        change.Status = PendingPasswordChangeStatus.Parked;
        change.AttemptCount = 5;
        change.FailureReason = PasswordSetFailureReason.ConfigurationFault;
        change.NextRetryAt = now.AddHours(3);

        change.Retry();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.NextRetryAt, Is.Null);
            Assert.That(change.AttemptCount, Is.Zero,
                "The retry budget is per configuration attempt; an administrator's fix earns a fresh one.");
            Assert.That(change.IsDue(now), Is.True);
        }
    }

    [Test]
    public void Expire_RecordsTheOutcomeRatherThanClearingTheHistory()
    {
        // Requirement 9: an expiry is a recorded outcome. The reason it last failed is what tells an
        // administrator why it never landed.
        var change = Change();
        change.FailureReason = PasswordSetFailureReason.Transient;
        change.TargetMessage = "Server unavailable";
        change.AttemptCount = 3;

        change.Expire();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
            Assert.That(change.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
            Assert.That(change.TargetMessage, Is.EqualTo("Server unavailable"));
            Assert.That(change.AttemptCount, Is.EqualTo(3));
            Assert.That(change.NextRetryAt, Is.Null);
        }
    }

    [Test]
    public void Cancel_RecordsTheOutcomeRatherThanClearingTheRow()
    {
        // The administrator's counterpart to Expire: a change nobody wants delivered any more. Recorded rather
        // than deleted, for the same reason an expiry is: the identity's password stays divergent in that system,
        // and a row that vanishes says the opposite.
        var now = new DateTime(2026, 8, 21, 9, 30, 0, DateTimeKind.Utc);
        var administratorId = Guid.NewGuid();

        var change = Change();
        change.Status = PendingPasswordChangeStatus.Parked;
        change.AttemptCount = 4;
        change.FailureReason = PasswordSetFailureReason.PolicyRejection;
        change.TargetMessage = "Password does not meet complexity requirements";

        change.Cancel(administratorId, "Ada Lovelace", now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
            Assert.That(change.CancelledAt, Is.EqualTo(now));
            Assert.That(change.CancelledById, Is.EqualTo(administratorId));
            Assert.That(change.CancelledByName, Is.EqualTo("Ada Lovelace"));
            Assert.That(change.NextRetryAt, Is.Null);
            Assert.That(change.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection),
                "Why it was stuck is why it was cancelled; losing it would leave the row unexplained.");
            Assert.That(change.TargetMessage, Is.EqualTo("Password does not meet complexity requirements"));
            Assert.That(change.AttemptCount, Is.EqualTo(4));
        }
    }

    [Test]
    public void Cancel_ChangeThatWasStillWaiting_IsNoLongerDue()
    {
        // A cancelled change must drop out of the delivery pass, exactly as a parked or expired one does.
        var now = new DateTime(2026, 8, 21, 9, 30, 0, DateTimeKind.Utc);

        var change = Change();
        change.ExpiresAt = now.AddDays(1);

        Assert.That(change.IsDue(now), Is.True, "Guard: the change starts out due.");

        change.Cancel(Guid.NewGuid(), "Ada Lovelace", now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.IsDue(now), Is.False);
            Assert.That(change.HasExpired(now.AddDays(2)), Is.False,
                "A cancelled change must not later be re-stamped as expired: it already has its outcome.");
        }
    }

    [Test]
    public void Cancel_WithNoNamedAdministrator_StillRecordsTheOutcome()
    {
        // The API-key path has no person behind it. The outcome and its instant still matter; only the name is
        // unknown, and recording "cancelled by nobody" beats not recording the cancellation.
        var now = new DateTime(2026, 8, 21, 9, 30, 0, DateTimeKind.Utc);

        var change = Change();
        change.Cancel(null, null, now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
            Assert.That(change.CancelledAt, Is.EqualTo(now));
            Assert.That(change.CancelledById, Is.Null);
            Assert.That(change.CancelledByName, Is.Null);
        }
    }

    [Test]
    public void Retry_AfterCancel_PutsTheChangeBackInTheQueueAndClearsTheCancellation()
    {
        // Cancelling is not final in the way expiring is: the password is still held, so an administrator who
        // cancelled by mistake can put it back. What must not survive is the cancellation stamp, which would
        // leave a pending row claiming to have been cancelled.
        var now = new DateTime(2026, 8, 21, 9, 30, 0, DateTimeKind.Utc);

        var change = Change();
        change.Cancel(Guid.NewGuid(), "Ada Lovelace", now);

        change.Retry();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.CancelledAt, Is.Null);
            Assert.That(change.CancelledById, Is.Null);
            Assert.That(change.CancelledByName, Is.Null);
        }
    }

    [Test]
    public void Supersede_AfterCancel_ClearsTheCancellation()
    {
        // A newer password change for the same target revives the row (requirement 8). It must not carry the
        // previous cancellation forward: that cancellation was of a password nobody is delivering any more.
        var now = new DateTime(2026, 8, 21, 9, 30, 0, DateTimeKind.Utc);

        var change = Change();
        change.Cancel(Guid.NewGuid(), "Ada Lovelace", now);

        change.Supersede(Newer("$JIMPW$v1$newer", PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
            Guid.NewGuid(), TimeSpan.FromHours(12), now.AddMinutes(5)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.CancelledAt, Is.Null);
            Assert.That(change.CancelledById, Is.Null);
            Assert.That(change.CancelledByName, Is.Null);
        }
    }

    private static ConnectedSystemPasswordSynchronisation Configuration() => new()
    {
        ConnectedSystemId = 3,
        Enabled = true,
        TargetObjectTypeId = 200,
        MaxRetries = 3,
        RetryBackoffBase = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// The claim (#1635): a deliverer takes the change, and only the status and the stamp change. Everything
    /// describing the password and its attempts survives, because a claim is a promise to attempt rather than
    /// an attempt.
    /// </summary>
    [Test]
    public void Claim_MarksTheChangeDeliveringAndStampsWho()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.AttemptCount = 2;

        change.Claim("worker-1a2b3c4d", now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Delivering));
            Assert.That(change.ClaimedAt, Is.EqualTo(now));
            Assert.That(change.ClaimedBy, Is.EqualTo("worker-1a2b3c4d"));
            Assert.That(change.AttemptCount, Is.EqualTo(2), "A claim is not an attempt.");
            Assert.That(change.IsDue(now), Is.False, "A claimed change is being delivered, not waiting to be.");
        }
    }

    [Test]
    public void IsClaimExpired_WithinTheLease_IsFalse()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.Claim("worker-1a2b3c4d", now);

        Assert.That(change.IsClaimExpired(now.AddSeconds(59), TimeSpan.FromSeconds(60)), Is.False);
    }

    [Test]
    public void IsClaimExpired_AtOrAfterTheLease_IsTrue()
    {
        // A deliverer that died mid-flight leaves the row Delivering. The lease running out is the only way
        // back that does not pass through that deliverer's own outcome write.
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.Claim("worker-1a2b3c4d", now);

        Assert.That(change.IsClaimExpired(now.AddSeconds(60), TimeSpan.FromSeconds(60)), Is.True);
    }

    [Test]
    public void IsClaimExpired_WhenNotClaimed_IsFalse()
    {
        var change = Change();

        Assert.That(change.IsClaimExpired(DateTime.UtcNow.AddDays(1), TimeSpan.FromSeconds(60)), Is.False);
    }

    [Test]
    public void ReleaseClaim_PutsTheChangeBackToPendingUnattempted()
    {
        // A lane that claimed and then found it could not deliver at all (no password capability, channel
        // refused, cancelled) gives the change back exactly as it was: still due, nothing counted against it.
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.Claim("worker-1a2b3c4d", now);

        change.ReleaseClaim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
            Assert.That(change.AttemptCount, Is.Zero);
            Assert.That(change.IsDue(now), Is.True);
        }
    }

    /// <summary>
    /// A delivery that would enable an account while an export to it is still in flight waits for that export
    /// (#2173): nothing is sent, so nothing is counted against the change, and what earlier attempts recorded stays.
    /// </summary>
    [Test]
    public void Postpone_PutsTheChangeBackToPendingUntilThenWithNothingCounted()
    {
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.ExpiresAt = now.AddDays(7);
        change.AttemptCount = 2;
        change.FailureReason = PasswordSetFailureReason.Transient;
        change.TargetMessage = "The server is unavailable.";
        change.Claim("worker-1a2b3c4d", now);

        change.Postpone(now.AddSeconds(15));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.NextRetryAt, Is.EqualTo(now.AddSeconds(15)));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
            Assert.That(change.AttemptCount, Is.EqualTo(2), "a wait is not an attempt");
            Assert.That(change.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
            Assert.That(change.TargetMessage, Is.EqualTo("The server is unavailable."));
            Assert.That(change.IsDue(now), Is.False);
            Assert.That(change.IsDue(now.AddSeconds(15)), Is.True);
        }
    }

    [Test]
    public void Postpone_NeverPastTheExpiry()
    {
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.ExpiresAt = now.AddSeconds(5);
        change.Claim("worker-1a2b3c4d", now);

        change.Postpone(now.AddSeconds(15));

        Assert.That(change.NextRetryAt, Is.EqualTo(change.ExpiresAt));
    }

    [Test]
    public void Postpone_OnAnUnclaimedChange_ChangesNothing()
    {
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.ExpiresAt = now.AddDays(7);
        change.Status = PendingPasswordChangeStatus.Parked;

        change.Postpone(now.AddSeconds(15));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(change.NextRetryAt, Is.Null);
        }
    }

    [Test]
    public void ReleaseClaim_OnAnUnclaimedChange_ChangesNothing()
    {
        var change = Change();
        change.Status = PendingPasswordChangeStatus.Parked;

        change.ReleaseClaim();

        Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked),
            "Releasing is only for a claim; it must not un-park a change nobody holds.");
    }

    [Test]
    public void RecordAttempt_EndsTheClaim()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.Claim("worker-1a2b3c4d", now);

        change.RecordAttempt(PasswordSetFailureReason.Transient, "Server unavailable", Configuration(), now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
        }
    }

    [Test]
    public void RecordAttempt_ThatParks_EndsTheClaim()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.Claim("worker-1a2b3c4d", now);

        change.RecordAttempt(PasswordSetFailureReason.PolicyRejection, "Too short", Configuration(), now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
        }
    }

    [Test]
    public void Retry_EndsTheClaim()
    {
        var change = Change();
        change.Claim("worker-1a2b3c4d", DateTime.UtcNow);

        change.Retry();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
        }
    }

    [Test]
    public void Cancel_EndsTheClaim()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.Claim("worker-1a2b3c4d", now);

        change.Cancel(Guid.NewGuid(), "Ada Lovelace", now.AddSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
        }
    }

    [Test]
    public void Expire_EndsTheClaim()
    {
        var change = Change();
        change.Claim("worker-1a2b3c4d", DateTime.UtcNow);

        change.Expire();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
        }
    }

    [Test]
    public void Supersede_EndsTheClaim()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.Claim("worker-1a2b3c4d", now);

        change.Supersede(Newer("$JIMPW$v1$newer", PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
            Guid.NewGuid(), TimeSpan.FromHours(12), now.AddSeconds(5)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
        }
    }

    [Test]
    public void HasExpired_WhileDelivering_IsFalse()
    {
        // Expiry never touches a claimed change: the deliverer holding it records its own outcome, and two
        // writers deciding the same row's fate at once is what the claim exists to prevent.
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.ExpiresAt = now.AddDays(-1);
        change.Claim("worker-1a2b3c4d", now);

        Assert.That(change.HasExpired(now), Is.False);
    }

    #region origin (#1635)

    /// <summary>
    /// Every row queued before origins existed was a propagated change, and the column defaults to that so the
    /// migration needs no backfill. An explicit set is the case that has to be said out loud.
    /// </summary>
    [Test]
    public void NewChange_IsPropagatedWithNoEnableDecision()
    {
        var change = Change();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(change.EnableAccount, Is.Null);
            Assert.That(change.IsExplicit, Is.False);
        }
    }

    /// <summary>
    /// Coalescing is by identity and system, whatever the origin: an administrator's reset arriving after a
    /// propagated change replaces it, and a later propagated change replaces the reset. The row carries the
    /// origin of the password it is now holding, because that is what decides how it is delivered.
    /// </summary>
    [Test]
    public void Supersede_ReplacesTheOriginAndTheEnableDecision()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();

        change.Supersede(Newer("$JIMPW$v1$reset", PasswordExpiryBehaviour.RequireChangeAtNextSignIn, Guid.NewGuid(),
            TimeSpan.FromDays(7), now, PendingPasswordChangeOrigin.Explicit, enableAccount: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            Assert.That(change.EnableAccount, Is.True);
            Assert.That(change.IsExplicit, Is.True);
        }

        change.Supersede(Newer("$JIMPW$v1$propagated", PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, Guid.NewGuid(),
            TimeSpan.FromDays(7), now.AddMinutes(1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(change.EnableAccount, Is.Null,
                "A propagated password never carries an enable decision, and must not inherit the reset's.");
        }
    }

    /// <summary>
    /// Whether an export wrote the account's enabled state (#2064) describes the account a Provisioned row was staged
    /// for, so it follows the row the same way: a re-provisioned account's new row carries its own export's answer,
    /// and an administrator's reset replacing a Provisioned row carries none.
    /// </summary>
    [Test]
    public void Supersede_CarriesTheNewerRowsEnabledStateSetByAttributeFlow()
    {
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();

        var reprovisioned = Newer(null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, Guid.NewGuid(),
            TimeSpan.FromDays(7), now, PendingPasswordChangeOrigin.Provisioned, syncRuleId: 12);
        reprovisioned.EnabledStateSetByAttributeFlow = true;
        change.Supersede(reprovisioned);

        Assert.That(change.EnabledStateSetByAttributeFlow, Is.True);

        change.Supersede(Newer("$JIMPW$v1$reset", PasswordExpiryBehaviour.RequireChangeAtNextSignIn, Guid.NewGuid(),
            TimeSpan.FromDays(7), now.AddMinutes(1), PendingPasswordChangeOrigin.Explicit));

        Assert.That(change.EnabledStateSetByAttributeFlow, Is.False,
            "An administrator's reset carries no export's answer, and must not inherit the provisioned row's.");
    }

    [Test]
    public void Supersede_TakesTheNewerChangesAccount()
    {
        // An explicit row names its account; a propagated row may not have one yet. Whichever arrives later is
        // the truth about which account the password is for.
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var accountId = Guid.NewGuid();
        var change = Change();
        var newer = Newer("$JIMPW$v1$reset", PasswordExpiryBehaviour.RequireChangeAtNextSignIn, Guid.NewGuid(),
            TimeSpan.FromDays(7), now, PendingPasswordChangeOrigin.Explicit);
        newer.ConnectedSystemObjectId = accountId;

        change.Supersede(newer);

        Assert.That(change.ConnectedSystemObjectId, Is.EqualTo(accountId));
    }

    /// <summary>
    /// The Synchronisation Rule follows the same rule as the origin and the enable decision: it describes the
    /// password the row is carrying now, so a newer row's value replaces the older one's, whatever it is.
    /// </summary>
    [Test]
    public void Supersede_CarriesTheNewerRowsSyncRuleId()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var syncRuleId = 42;
        var change = Change();

        change.Supersede(Newer(null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, Guid.NewGuid(),
            TimeSpan.FromDays(7), now, PendingPasswordChangeOrigin.Provisioned, syncRuleId: syncRuleId));

        Assert.That(change.SyncRuleId, Is.EqualTo(syncRuleId));
    }

    /// <summary>
    /// Passwords replace each other; promises about the account carry forward (#2160). An administrator's reset
    /// replacing an Initial Password still owed takes the password, the expiry and the enable choice, and keeps the
    /// account's first-password promises: the rule they come from, the account they are for, and whether an export
    /// wrote its flags. This replaces the behaviour pinned before #2160, which cleared the rule and with it the enable.
    /// </summary>
    [Test]
    public void Supersede_ExplicitOverALiveProvisionedRow_TakesThePasswordAndKeepsTheInitialPasswordsPromises()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var accountId = Guid.NewGuid();
        var change = Provisioned(accountId, syncRuleId: 42, accountFlagsWrittenByExport: true, now);

        var reset = Newer("$JIMPW$v1$reset", PasswordExpiryBehaviour.RequireChangeAtNextSignIn, Guid.NewGuid(),
            TimeSpan.FromDays(7), now.AddMinutes(1), PendingPasswordChangeOrigin.Explicit, enableAccount: true);
        reset.ConnectedSystemObjectId = accountId;
        change.Supersede(reset);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            Assert.That(change.EncryptedPassword, Is.EqualTo("$JIMPW$v1$reset"));
            Assert.That(change.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
            Assert.That(change.EnableAccount, Is.True);
            Assert.That(change.SyncRuleId, Is.EqualTo(42), "the rule whose promises the reset now carries");
            Assert.That(change.ProvisionedConnectedSystemObjectId, Is.EqualTo(accountId));
            Assert.That(change.EnabledStateSetByAttributeFlow, Is.True);
            Assert.That(change.CarriesInitialPassword, Is.True);
        }
    }

    /// <summary>
    /// The reverse order of #2160: the Initial Password was queued, and the person's own password change arrived
    /// before it landed. The person's password is delivered, with the Initial Password's promises about the account.
    /// Every live state counts, a parked one included: the account is still owed its first password.
    /// </summary>
    [TestCase(PendingPasswordChangeStatus.Pending)]
    [TestCase(PendingPasswordChangeStatus.Delivering)]
    [TestCase(PendingPasswordChangeStatus.Parked)]
    public void Supersede_ByAPropagatedChange_KeepsALiveRowsInitialPasswordPromises(PendingPasswordChangeStatus status)
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var accountId = Guid.NewGuid();
        var change = Provisioned(accountId, syncRuleId: 42, accountFlagsWrittenByExport: true, now);
        change.Status = status;

        change.Supersede(Newer("$JIMPW$v1$propagated", PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, Guid.NewGuid(),
            TimeSpan.FromDays(7), now.AddMinutes(1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(change.EncryptedPassword, Is.EqualTo("$JIMPW$v1$propagated"));
            Assert.That(change.SyncRuleId, Is.EqualTo(42));
            Assert.That(change.ProvisionedConnectedSystemObjectId, Is.EqualTo(accountId));
            Assert.That(change.EnabledStateSetByAttributeFlow, Is.True);
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
        }
    }

    /// <summary>
    /// A change already standing in for the Initial Password, replaced by a newer one, passes the promises on again.
    /// </summary>
    [Test]
    public void Supersede_OfAChangeCarryingAFirstPassword_PassesThePromisesOn()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var accountId = Guid.NewGuid();
        var change = Change();
        change.ExpiresAt = now.AddDays(1);
        change.CarryInitialPassword(accountId, syncRuleId: 42, accountFlagsWrittenByExport: false, now.AddDays(7));

        change.Supersede(Newer("$JIMPW$v1$newer", PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, Guid.NewGuid(),
            TimeSpan.FromDays(7), now.AddMinutes(1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.SyncRuleId, Is.EqualTo(42));
            Assert.That(change.ProvisionedConnectedSystemObjectId, Is.EqualTo(accountId));
            Assert.That(change.EncryptedPassword, Is.EqualTo("$JIMPW$v1$newer"));
        }
    }

    /// <summary>
    /// An expired or cancelled row owes nothing any more: what replaces it carries its own promises, or none.
    /// </summary>
    [TestCase(PendingPasswordChangeStatus.Expired)]
    [TestCase(PendingPasswordChangeStatus.Cancelled)]
    public void Supersede_OfAnExpiredOrCancelledRow_DropsThePromises(PendingPasswordChangeStatus status)
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var change = Provisioned(Guid.NewGuid(), syncRuleId: 42, accountFlagsWrittenByExport: true, now);
        change.Status = status;

        change.Supersede(Newer("$JIMPW$v1$propagated", PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, Guid.NewGuid(),
            TimeSpan.FromDays(7), now.AddMinutes(1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.SyncRuleId, Is.Null);
            Assert.That(change.ProvisionedConnectedSystemObjectId, Is.Null);
            Assert.That(change.EnabledStateSetByAttributeFlow, Is.False);
            Assert.That(change.CarriesInitialPassword, Is.False);
        }
    }

    /// <summary>
    /// A newer Initial Password (the account deleted and provisioned again) brings promises of its own, which win.
    /// </summary>
    [Test]
    public void Supersede_ByAProvisionedChange_ReplacesThePromises()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var change = Provisioned(Guid.NewGuid(), syncRuleId: 42, accountFlagsWrittenByExport: true, now);
        var reprovisionedAccount = Guid.NewGuid();

        change.Supersede(Provisioned(reprovisionedAccount, syncRuleId: 43, accountFlagsWrittenByExport: false, now.AddMinutes(1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.SyncRuleId, Is.EqualTo(43));
            Assert.That(change.ProvisionedConnectedSystemObjectId, Is.EqualTo(reprovisionedAccount));
            Assert.That(change.EnabledStateSetByAttributeFlow, Is.False);
        }
    }

    /// <summary>
    /// The mirror image: a provisioned row replacing an expired propagated one carries a rule and no password,
    /// and must not leave the previous row's password behind.
    /// </summary>
    [Test]
    public void Supersede_ProvisionedOverExpiredPropagated_LeavesNoPasswordBehind()
    {
        var now = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.Status = PendingPasswordChangeStatus.Expired;

        change.Supersede(Newer(null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, Guid.NewGuid(),
            TimeSpan.FromDays(7), now, PendingPasswordChangeOrigin.Provisioned, syncRuleId: 42));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(change.SyncRuleId, Is.EqualTo(42));
            Assert.That(change.EncryptedPassword, Is.Null);
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
        }
    }

    /// <summary>
    /// A provisioned row is neither an administrator's explicit set nor an ordinary propagation: it is a third,
    /// distinct origin, and the two existing predicates must not accidentally claim it.
    /// </summary>
    [Test]
    public void IsProvisioned_IsNeitherExplicitNorPropagated()
    {
        var change = Change();
        change.Origin = PendingPasswordChangeOrigin.Provisioned;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.IsProvisioned, Is.True);
            Assert.That(change.IsExplicit, Is.False);
            Assert.That(change.IsPropagated, Is.False);
        }
    }

    #endregion

    #region carrying an account's first password (#2160)

    [Test]
    public void CarryInitialPassword_KeepsTheChangesOwnPasswordAndTakesTheAccountsPromises()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var accountId = Guid.NewGuid();
        var change = Change();
        var activityId = change.ActivityId;
        change.ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy;
        change.CreatedAt = now.AddHours(-1);
        change.ExpiresAt = now.AddDays(6);

        change.CarryInitialPassword(accountId, syncRuleId: 42, accountFlagsWrittenByExport: true, now.AddDays(7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(change.EncryptedPassword, Is.EqualTo("$JIMPW$v1$ciphertext"));
            Assert.That(change.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy),
                "\"must change at next sign-in\" belongs to the password, never to the account's promises");
            Assert.That(change.ActivityId, Is.EqualTo(activityId));
            Assert.That(change.ConnectedSystemObjectId, Is.EqualTo(accountId), "a propagated change now has its account");
            Assert.That(change.ProvisionedConnectedSystemObjectId, Is.EqualTo(accountId));
            Assert.That(change.SyncRuleId, Is.EqualTo(42));
            Assert.That(change.EnabledStateSetByAttributeFlow, Is.True);
            Assert.That(change.CarriesInitialPassword, Is.True);
            Assert.That(change.OwesInitialPassword, Is.True);
            Assert.That(change.IsDue(now), Is.True, "the account now exists, so the change is due at once");
        }
    }

    /// <summary>
    /// A deliverer holding the change decided its flags without the promises, so its claim is taken back, as
    /// <see cref="PendingPasswordChange.Supersede"/> does: its outcome write and its delete are guarded on the row
    /// still being Delivering, and land nowhere. The password is delivered again, with the promises, on its own claim.
    /// </summary>
    [Test]
    public void CarryInitialPassword_OnADeliveringRow_TakesTheClaimBack()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.ExpiresAt = now.AddDays(1);
        change.Claim("worker-1", now);

        change.CarryInitialPassword(Guid.NewGuid(), syncRuleId: 42, accountFlagsWrittenByExport: false, now.AddDays(7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
        }
    }

    /// <summary>
    /// A change parked because the account did not exist yet, or because the target could not be reached until the
    /// retries ran out, is released: the Create that provisioned the account has just proved both wrong. Its attempts
    /// are reset, because they counted against circumstances that no longer hold.
    /// </summary>
    [TestCase(PasswordSetFailureReason.TargetObjectNotFound)]
    [TestCase(PasswordSetFailureReason.Transient)]
    public void CarryInitialPassword_OnARowParkedForAReasonTheAccountCures_ReleasesItAndResetsItsAttempts(PasswordSetFailureReason reason)
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.ExpiresAt = now.AddDays(1);
        change.Status = PendingPasswordChangeStatus.Parked;
        change.FailureReason = reason;
        change.TargetMessage = "The identity has no account in this Connected System yet.";
        change.AttemptCount = 6;

        change.CarryInitialPassword(Guid.NewGuid(), syncRuleId: 42, accountFlagsWrittenByExport: false, now.AddDays(7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.AttemptCount, Is.Zero);
            Assert.That(change.FailureReason, Is.Null);
            Assert.That(change.TargetMessage, Is.Null);
            Assert.That(change.NextRetryAt, Is.Null);
        }
    }

    /// <summary>
    /// A change waiting out a backoff for a missing account is due now; its attempts described a missing account and
    /// are reset. One retrying a transient failure keeps its count, which still describes the target.
    /// </summary>
    [Test]
    public void CarryInitialPassword_OnARowRetryingForAMissingAccount_MakesItDueAndResetsItsAttempts()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var change = Change();
        change.ExpiresAt = now.AddDays(1);
        change.FailureReason = PasswordSetFailureReason.TargetObjectNotFound;
        change.AttemptCount = 2;
        change.NextRetryAt = now.AddMinutes(20);

        change.CarryInitialPassword(Guid.NewGuid(), syncRuleId: 42, accountFlagsWrittenByExport: false, now.AddDays(7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.NextRetryAt, Is.Null);
            Assert.That(change.AttemptCount, Is.Zero);
            Assert.That(change.FailureReason, Is.Null);
        }
    }

    [Test]
    public void CarryInitialPassword_ExtendsButNeverShortensExpiresAt()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var shortLived = Change();
        shortLived.ExpiresAt = now.AddDays(1);
        var longLived = Change();
        longLived.ExpiresAt = now.AddDays(30);

        shortLived.CarryInitialPassword(Guid.NewGuid(), 42, false, now.AddDays(7));
        longLived.CarryInitialPassword(Guid.NewGuid(), 42, false, now.AddDays(7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shortLived.ExpiresAt, Is.EqualTo(now.AddDays(7)));
            Assert.That(longLived.ExpiresAt, Is.EqualTo(now.AddDays(30)));
        }
    }

    /// <summary>
    /// A propagated change is held while its Connected System's Password Synchronisation is switched off; one carrying
    /// an account's first password is not, like the Initial Password it stands in for (decision D1).
    /// </summary>
    [Test]
    public void IsHeldWhilePasswordSynchronisationIsOff_OnlyForAPropagatedChangeOwingNoFirstPassword()
    {
        var plain = Change();
        var carrying = Change();
        carrying.CarryInitialPassword(Guid.NewGuid(), 42, false, DateTime.UtcNow.AddDays(7));
        var explicitSet = Change();
        explicitSet.Origin = PendingPasswordChangeOrigin.Explicit;
        var provisioned = Provisioned(Guid.NewGuid(), 42, false, DateTime.UtcNow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plain.IsHeldWhilePasswordSynchronisationIsOff, Is.True);
            Assert.That(carrying.IsHeldWhilePasswordSynchronisationIsOff, Is.False);
            Assert.That(explicitSet.IsHeldWhilePasswordSynchronisationIsOff, Is.False);
            Assert.That(provisioned.IsHeldWhilePasswordSynchronisationIsOff, Is.False);
            Assert.That(provisioned.OwesInitialPassword, Is.True);
            Assert.That(provisioned.CarriesInitialPassword, Is.False, "an Initial Password is the first password, it does not carry one");
        }
    }

    #endregion
}
