// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The Exchange queue row (#2021): one Exchange change owed to one directory account. What is pinned here is the
/// declared-state comparison that decides whether re-staging leaves the account's row as it is, whatever its status.
/// </summary>
[TestFixture]
public class PendingExchangeChangeTests
{
    private static PendingExchangeChange Declared(
        string recipientType = "Mailbox",
        string? alias = "ada.lovelace",
        string? database = "DB01",
        string? primarySmtpAddress = "ada.lovelace@example.com",
        string? externalAddress = null) => new()
    {
        ConnectedSystemId = 3,
        ConnectedSystemObjectId = Guid.NewGuid(),
        ActivityId = Guid.NewGuid(),
        RecipientType = recipientType,
        Alias = alias,
        Database = database,
        PrimarySmtpAddress = primarySmtpAddress,
        ExternalAddress = externalAddress
    };

    [Test]
    public void HasSameDeclaredStateAs_AllFiveValuesEqual_ReturnsTrue()
    {
        var row = Declared(externalAddress: "SMTP:ada@partner.example");
        var other = Declared(externalAddress: "SMTP:ada@partner.example");

        Assert.That(row.HasSameDeclaredStateAs(other), Is.True);
    }

    [Test]
    public void HasSameDeclaredStateAs_NullAndEmptyInEachValue_AreTheSame()
    {
        var row = Declared(alias: null, database: "", primarySmtpAddress: null, externalAddress: "");
        var other = Declared(alias: "", database: null, primarySmtpAddress: "", externalAddress: null);

        Assert.That(row.HasSameDeclaredStateAs(other), Is.True);
    }

    [Test]
    public void HasSameDeclaredStateAs_ValuesDifferingOnlyByCase_AreDifferent()
    {
        // Ordinal, not case-insensitive: an alias or address declared with different casing is a different request,
        // and Exchange keeps the casing it is given.
        var row = Declared(alias: "ada.lovelace");
        var other = Declared(alias: "Ada.Lovelace");

        Assert.That(row.HasSameDeclaredStateAs(other), Is.False);
    }

    [TestCase("RecipientType")]
    [TestCase("Alias")]
    [TestCase("Database")]
    [TestCase("PrimarySmtpAddress")]
    [TestCase("ExternalAddress")]
    public void HasSameDeclaredStateAs_OneValueDiffers_ReturnsFalse(string differing)
    {
        var row = Declared(externalAddress: "SMTP:ada@partner.example");
        var other = Declared(externalAddress: "SMTP:ada@partner.example");
        switch (differing)
        {
            case "RecipientType": other.RecipientType = "SharedMailbox"; break;
            case "Alias": other.Alias = "ada"; break;
            case "Database": other.Database = "DB02"; break;
            case "PrimarySmtpAddress": other.PrimarySmtpAddress = "ada@example.com"; break;
            case "ExternalAddress": other.ExternalAddress = null; break;
        }

        Assert.That(row.HasSameDeclaredStateAs(other), Is.False, $"{differing} differs");
    }

    [Test]
    public void HasSameDeclaredStateAs_IgnoresEverythingButTheDeclaredState()
    {
        // The domain controller, the account, the rule and the Activity describe where and why the state was declared,
        // not what was declared, so a re-stage that differs only in those still matches what was parked.
        var row = Declared();
        row.DomainController = "dc01.corp.example";
        row.Status = PendingExchangeChangeStatus.Parked;
        var other = Declared();
        other.DomainController = "dc02.corp.example";
        other.SyncRuleId = 9;
        other.MetaverseObjectId = Guid.NewGuid();

        Assert.That(row.HasSameDeclaredStateAs(other), Is.True);
    }
    // ---- Delivery: the lane's state machine (Phase 5) ------------------------------------------------------------

    private static readonly DateTime AsOf = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private static PendingExchangeChange Claimed()
    {
        var change = Declared();
        change.Claim("worker-1:abcd1234", AsOf);
        return change;
    }

    [Test]
    public void IsDue_PendingWithNoRetryScheduled_IsDue()
    {
        Assert.That(Declared().IsDue(AsOf), Is.True);
    }

    [TestCase(PendingExchangeChangeStatus.Delivering)]
    [TestCase(PendingExchangeChangeStatus.Parked)]
    [TestCase(PendingExchangeChangeStatus.Refused)]
    public void IsDue_AnythingButPending_IsNotDue(PendingExchangeChangeStatus status)
    {
        var change = Declared();
        change.Status = status;

        Assert.That(change.IsDue(AsOf), Is.False);
    }

    [Test]
    public void IsDue_RetryStillAhead_IsNotDueUntilItArrives()
    {
        var change = Declared();
        change.NextRetryAt = AsOf.AddMinutes(2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.IsDue(AsOf), Is.False);
            Assert.That(change.IsDue(AsOf.AddMinutes(2)), Is.True);
        }
    }

    [Test]
    public void Claim_PendingChange_IsDeliveringUnderTheClaimantsStamp()
    {
        var change = Claimed();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExchangeChangeStatus.Delivering));
            Assert.That(change.ClaimedAt, Is.EqualTo(AsOf));
            Assert.That(change.ClaimedBy, Is.EqualTo("worker-1:abcd1234"));
            Assert.That(change.AttemptCount, Is.Zero, "a claim is a promise to attempt, not an attempt");
        }
    }

    [Test]
    public void IsClaimExpired_ClaimOlderThanTheLease_IsClaimableAgain()
    {
        var change = Claimed();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.IsClaimExpired(AsOf + PendingExchangeChange.ClaimLease - TimeSpan.FromSeconds(1), PendingExchangeChange.ClaimLease), Is.False);
            Assert.That(change.IsClaimExpired(AsOf + PendingExchangeChange.ClaimLease, PendingExchangeChange.ClaimLease), Is.True);
            Assert.That(Declared().IsClaimExpired(AsOf.AddDays(1), PendingExchangeChange.ClaimLease), Is.False, "nobody holds an unclaimed change");
        }
    }

    [Test]
    public void ReleaseClaim_ClaimedChange_GoesBackToPendingWithNothingCounted()
    {
        var change = Claimed();

        change.ReleaseClaim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(change.ClaimedAt, Is.Null);
            Assert.That(change.ClaimedBy, Is.Null);
            Assert.That(change.AttemptCount, Is.Zero);
            Assert.That(change.IsDue(AsOf), Is.True);
        }
    }

    [TestCase(1, 2)]
    [TestCase(2, 4)]
    [TestCase(3, 8)]
    [TestCase(4, 16)]
    [TestCase(5, 32)]
    [TestCase(6, 60)]
    [TestCase(20, 60)]
    public void RetryDelay_DoublesFromTwoMinutesToAnHourAtMost(int attemptCount, int expectedMinutes)
    {
        Assert.That(PendingExchangeChange.RetryDelay(attemptCount), Is.EqualTo(TimeSpan.FromMinutes(expectedMinutes)));
    }

    [TestCase(ExchangeChangeFailureKind.ObjectNotFound)]
    [TestCase(ExchangeChangeFailureKind.Unreachable)]
    [TestCase(ExchangeChangeFailureKind.AuthenticationFailed)]
    public void RecordFailure_FailureThatMayClear_RetriesAfterTheBackoff(ExchangeChangeFailureKind kind)
    {
        var change = Claimed();

        change.RecordFailure(kind, "Exchange's words", AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(change.NextRetryAt, Is.EqualTo(AsOf.AddMinutes(2)));
            Assert.That(change.AttemptCount, Is.EqualTo(1));
            Assert.That(change.LastAttemptedAt, Is.EqualTo(AsOf));
            Assert.That(change.FirstFailedAt, Is.EqualTo(AsOf));
            Assert.That(change.FailureKind, Is.EqualTo(kind));
            Assert.That(change.FailureMessage, Is.EqualTo("Exchange's words"));
            Assert.That(change.ClaimedAt, Is.Null, "an attempt ends the claim");
            Assert.That(change.ClaimedBy, Is.Null);
        }
    }

    [TestCase(ExchangeChangeFailureKind.Refused)]
    [TestCase(ExchangeChangeFailureKind.InsufficientPermissions)]
    public void RecordFailure_FailureNoRetryCanCure_ParksAtOnce(ExchangeChangeFailureKind kind)
    {
        var change = Claimed();

        change.RecordFailure(kind, "Exchange's words", AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(change.NextRetryAt, Is.Null);
            Assert.That(change.AttemptCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void RecordFailure_LaterFailures_KeepWhenTheRunOfFailuresBegan()
    {
        var change = Claimed();
        change.RecordFailure(ExchangeChangeFailureKind.Unreachable, null, AsOf);
        change.Claim("worker-1:abcd1234", AsOf.AddMinutes(2));

        change.RecordFailure(ExchangeChangeFailureKind.Unreachable, null, AsOf.AddMinutes(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.FirstFailedAt, Is.EqualTo(AsOf));
            Assert.That(change.AttemptCount, Is.EqualTo(2));
            Assert.That(change.NextRetryAt, Is.EqualTo(AsOf.AddMinutes(6)));
        }
    }

    [Test]
    public void RecordFailure_StillFailingTwentyFourHoursOn_Parks()
    {
        var change = Claimed();
        change.RecordFailure(ExchangeChangeFailureKind.Unreachable, null, AsOf);
        change.Claim("worker-1:abcd1234", AsOf.AddHours(24));

        change.RecordFailure(ExchangeChangeFailureKind.Unreachable, null, AsOf.AddHours(24));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(change.NextRetryAt, Is.Null);
        }
    }

    [Test]
    public void RecordFailure_BackoffWouldPassTheTwentyFourHours_LastAttemptLandsOnThem()
    {
        // An hour's backoff from 23h30m in would book the next attempt half an hour after the change should have parked;
        // it is booked for the 24-hour mark instead, so "parked after 24 hours of failures" holds to the minute.
        var change = Claimed();
        change.RecordFailure(ExchangeChangeFailureKind.Unreachable, null, AsOf);
        change.AttemptCount = 10;
        change.Claim("worker-1:abcd1234", AsOf.AddHours(23.5));

        change.RecordFailure(ExchangeChangeFailureKind.Unreachable, null, AsOf.AddHours(23.5));

        Assert.That(change.NextRetryAt, Is.EqualTo(AsOf.AddHours(24)));
    }

    [Test]
    public void RepeatsLastFailure_SameKindAndMessage_IsTrue_OtherwiseFalse()
    {
        var change = Claimed();
        Assert.That(change.RepeatsLastFailure(ExchangeChangeFailureKind.Unreachable, "down"), Is.False, "no failure yet");

        change.RecordFailure(ExchangeChangeFailureKind.Unreachable, "down", AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.RepeatsLastFailure(ExchangeChangeFailureKind.Unreachable, "down"), Is.True);
            Assert.That(change.RepeatsLastFailure(ExchangeChangeFailureKind.Unreachable, "timed out"), Is.False);
            Assert.That(change.RepeatsLastFailure(ExchangeChangeFailureKind.AuthenticationFailed, "down"), Is.False);
        }
    }
}
