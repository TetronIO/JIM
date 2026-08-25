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
/// Unlike <see cref="PendingInitialPassword"/>, which records only that an account is owed a password, this row
/// carries the password itself. That single difference is what forces everything distinctive about it: the value
/// is encrypted at rest, a newer change for the same target must replace an older one rather than queue behind
/// it, and delivery is scheduled on a clock of its own rather than riding an export run.
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

        change.Supersede("$JIMPW$v1$newer", PasswordExpiryBehaviour.NeverExpires, newActivityId,
            TimeSpan.FromDays(7), now);

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

        change.Supersede("$JIMPW$v1$newer", PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
            Guid.NewGuid(), TimeSpan.FromHours(12), now.AddMinutes(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.CancelledAt, Is.Null);
            Assert.That(change.CancelledById, Is.Null);
            Assert.That(change.CancelledByName, Is.Null);
        }
    }
}
