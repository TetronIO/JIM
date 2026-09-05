// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The derivation behind the person page's password history timeline (#1635): which day an entry sits under, what
/// colour its dot is, what each Connected System's chip says, and which of them earn a line of words beneath.
/// <para>
/// Pure, so the rules can be pinned without rendering anything. The one rule worth the most care is how a live
/// queue row is matched to a change: the queue coalesces per person and system, refreshing the row's CreatedAt to
/// the newest change's time, so a row belongs to the newest change created at or before it, and it must never
/// recolour an older change that has already finished with that system.
/// </para>
/// </summary>
[TestFixture]
public class PasswordHistoryTimelineModelTests
{
    /// <summary>A fixed "now" so Today and Yesterday do not depend on the wall clock.</summary>
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private static PasswordSynchronisationEvent Change(
        DateTime created,
        PendingPasswordChangeOrigin? origin = PendingPasswordChangeOrigin.Explicit,
        string? initiator = "Admin User",
        ActivityInitiatorType initiatorType = ActivityInitiatorType.User,
        params PasswordSynchronisationEventOutcome[] outcomes) => new()
    {
        ActivityId = Guid.NewGuid(),
        Created = created,
        InitiatedByName = initiator,
        InitiatedByType = initiatorType,
        Origin = origin,
        Outcomes = outcomes
    };

    private static PasswordSynchronisationEventOutcome Outcome(int systemId, string system, ActivityStatus status, DateTime occurredAt, string? error = null) => new()
    {
        ActivityId = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        ConnectedSystemName = system,
        Status = status,
        ErrorMessage = error,
        Message = error ?? $"Password set on {system}.",
        OccurredAt = occurredAt
    };

    private static PendingPasswordChangeHeader Row(
        int systemId,
        string system,
        PendingPasswordChangeStatus status,
        DateTime createdAt,
        PasswordSetFailureReason? reason = null,
        string? targetMessage = null,
        int attempts = 0,
        DateTime? nextRetryAt = null,
        bool taking = true,
        PendingPasswordChangeOrigin origin = PendingPasswordChangeOrigin.Explicit,
        string? cancelledBy = null) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        ConnectedSystemName = system,
        Status = status,
        FailureReason = reason,
        TargetMessage = targetMessage,
        AttemptCount = attempts,
        NextRetryAt = nextRetryAt,
        CreatedAt = createdAt,
        LastAttemptedAt = attempts > 0 ? createdAt.AddSeconds(5) : null,
        ExpiresAt = createdAt.AddDays(7),
        ConnectedSystemTakingPasswords = taking,
        Origin = origin,
        CancelledByName = cancelledBy
    };

    private static PasswordHistoryTimelineModel.Entry Single(IReadOnlyList<PasswordHistoryTimelineModel.Day> days) =>
        days.SelectMany(d => d.Entries).Single();

    #region days

    [Test]
    public void Build_ChangesAcrossThreeDays_GroupsThemUnderTodayYesterdayAndTheDate()
    {
        var threeDaysAgo = Now.AddDays(-3);
        var days = PasswordHistoryTimelineModel.Build(
        [
            Change(Now.AddMinutes(-5)),
            Change(Now.AddDays(-1)),
            Change(threeDaysAgo)
        ], [], Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(days.Select(d => d.Heading), Is.EqualTo(new[] { "Today", "Yesterday", threeDaysAgo.ToLocalTime().ToFriendlyDay() }));
            Assert.That(days.Select(d => d.Entries.Count), Is.All.EqualTo(1));
        }
    }

    [Test]
    public void Build_TwoChangesOnOneDay_SitUnderOneHeadingNewestFirst()
    {
        var earlier = Change(Now.AddHours(-3));
        var later = Change(Now.AddHours(-1));

        var days = PasswordHistoryTimelineModel.Build([earlier, later], [], Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(days, Has.Count.EqualTo(1));
            Assert.That(days[0].Entries.Select(e => e.ActivityId), Is.EqualTo(new[] { later.ActivityId, earlier.ActivityId }));
            Assert.That(days[0].Entries[0].LocalTime, Is.EqualTo(later.Created.ToLocalTime()));
        }
    }

    #endregion

    #region the entry line

    [Test]
    public void Build_ExplicitChangeOnSeveralAccounts_SaysByWhomAndOnHowMany()
    {
        var entry = Single(PasswordHistoryTimelineModel.Build(
        [
            Change(Now, outcomes:
            [
                Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now.AddSeconds(2)),
                Outcome(2, "HR Portal", ActivityStatus.Complete, Now.AddSeconds(3)),
                Outcome(3, "Badge System", ActivityStatus.Complete, Now.AddSeconds(4))
            ])
        ], [], Now));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.InitiatorLead, Is.EqualTo("by"));
            Assert.That(entry.InitiatorName, Is.EqualTo("Admin User"));
            Assert.That(entry.InitiatorTrail, Is.Null);
            Assert.That(entry.Scope, Is.EqualTo("on 3 Connected Systems"));
        }
    }

    [Test]
    public void Build_ExplicitChangeOnOneAccount_OmitsTheCount()
    {
        var entry = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now, outcomes: Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now))], [], Now));

        Assert.That(entry.Scope, Is.Null);
    }

    [Test]
    public void Build_PropagatedChangeFromAnApiKey_ReadsViaTheKeyWithNoCount()
    {
        var entry = Single(PasswordHistoryTimelineModel.Build(
        [
            Change(Now, PendingPasswordChangeOrigin.Propagated, "Service Desk", ActivityInitiatorType.ApiKey,
                Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now),
                Outcome(2, "HR Portal", ActivityStatus.Complete, Now))
        ], [], Now));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.InitiatorLead, Is.EqualTo("via"));
            Assert.That(entry.InitiatorName, Is.EqualTo("Service Desk"));
            Assert.That(entry.InitiatorTrail, Is.EqualTo("(API key)"));
            Assert.That(entry.Scope, Is.Null, "the count is for an administrator's explicit choice of Connected Systems");
        }
    }

    [Test]
    public void Build_ChangeWithNoInitiatorRecorded_HasNoName()
    {
        var entry = Single(PasswordHistoryTimelineModel.Build([Change(Now, initiator: null)], [], Now));

        Assert.That(entry.InitiatorName, Is.Null);
    }

    #endregion

    #region the dot

    [Test]
    public void Build_EverySystemSet_ColoursTheDotGreen()
    {
        var entry = Single(PasswordHistoryTimelineModel.Build(
        [
            Change(Now, outcomes:
            [
                Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now),
                Outcome(2, "HR Portal", ActivityStatus.CompleteWithWarning, Now)
            ])
        ], [], Now));

        Assert.That(entry.DotColour, Is.EqualTo(Color.Success));
    }

    [Test]
    public void Build_OneSystemRetrying_ColoursTheDotAmber()
    {
        var entry = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now, outcomes: [Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now), Outcome(2, "HR Portal", ActivityStatus.FailedWithError, Now, "Connection refused")])],
            [Row(2, "HR Portal", PendingPasswordChangeStatus.Pending, Now, PasswordSetFailureReason.Transient, "Connection refused", attempts: 1, nextRetryAt: Now.AddMinutes(5))],
            Now));

        Assert.That(entry.DotColour, Is.EqualTo(Color.Warning));
    }

    [Test]
    public void Build_OneSystemParked_ColoursTheDotRedWhateverTheOthersDid()
    {
        var entry = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now, outcomes: [Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now), Outcome(2, "HR Portal", ActivityStatus.FailedWithError, Now, "Too short.")])],
            [
                Row(2, "HR Portal", PendingPasswordChangeStatus.Parked, Now, PasswordSetFailureReason.PolicyRejection, "Too short.", attempts: 3),
                Row(3, "Badge System", PendingPasswordChangeStatus.Pending, Now, attempts: 1, nextRetryAt: Now.AddMinutes(1))
            ],
            Now));

        Assert.That(entry.DotColour, Is.EqualTo(Color.Error));
    }

    [Test]
    public void Build_NothingAttemptedYet_LeavesTheDotGrey()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Single(PasswordHistoryTimelineModel.Build([Change(Now)], [], Now)).DotColour, Is.EqualTo(Color.Default),
                "a change that reached nothing has no outcome to colour by");
            Assert.That(Single(PasswordHistoryTimelineModel.Build([Change(Now)], [Row(1, "Corporate Directory", PendingPasswordChangeStatus.Pending, Now)], Now)).DotColour,
                Is.EqualTo(Color.Default), "queued and not yet attempted is not an outcome either");
        }
    }

    #endregion

    #region chips and the queue-row override

    [Test]
    public void Build_LiveQueueRow_DecidesTheNewestChangesChipAndLeavesAnOlderOneAlone()
    {
        // Yesterday's change reached Corporate Directory; today's is still retrying there. The queue holds one row
        // per person and system, so the row is today's, and yesterday's success must keep reading as one.
        var yesterday = Change(Now.AddDays(-1), outcomes: Outcome(4, "Corporate Directory", ActivityStatus.Complete, Now.AddDays(-1).AddSeconds(3)));
        var today = Change(Now.AddMinutes(-10), outcomes: Outcome(4, "Corporate Directory", ActivityStatus.FailedWithError, Now.AddMinutes(-9), "Connection refused"));
        var row = Row(4, "Corporate Directory", PendingPasswordChangeStatus.Pending, Now.AddMinutes(-10), PasswordSetFailureReason.Transient, "Connection refused", attempts: 2, nextRetryAt: Now.AddMinutes(5));

        var days = PasswordHistoryTimelineModel.Build([today, yesterday], [row], Now);

        var todayTarget = days[0].Entries.Single().Targets.Single();
        var yesterdayTarget = days[1].Entries.Single().Targets.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(todayTarget.State, Is.EqualTo(PasswordHistoryTimelineModel.TargetState.Retrying));
            Assert.That(todayTarget.ChipSuffix, Is.EqualTo("retrying"));
            Assert.That(todayTarget.PillModifier, Is.EqualTo("warn"));
            Assert.That(yesterdayTarget.State, Is.EqualTo(PasswordHistoryTimelineModel.TargetState.Set));
            Assert.That(yesterdayTarget.ChipSuffix, Is.Null, "a plain success is the system's name alone");
            Assert.That(yesterdayTarget.PillModifier, Is.EqualTo("ok"));
        }
    }

    [Test]
    public void Build_QueueRowOlderThanTheNewestChange_BelongsToTheChangeThatCreatedIt()
    {
        // A held propagated row from an earlier change sits under that change, not under a later explicit set that
        // never targeted the system: the row's CreatedAt is refreshed only when a newer change supersedes it.
        var earlier = Change(Now.AddHours(-2), PendingPasswordChangeOrigin.Propagated);
        var later = Change(Now.AddHours(-1), outcomes: Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now.AddHours(-1)));
        var heldRow = Row(2, "HR Portal", PendingPasswordChangeStatus.Pending, Now.AddHours(-2), taking: false, origin: PendingPasswordChangeOrigin.Propagated);

        var entries = PasswordHistoryTimelineModel.Build([later, earlier], [heldRow], Now).SelectMany(d => d.Entries).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].Targets.Select(t => t.Name), Is.EqualTo(new[] { "Corporate Directory" }));
            Assert.That(entries[1].Targets.Select(t => (t.Name, t.State)),
                Is.EqualTo(new[] { ("HR Portal", PasswordHistoryTimelineModel.TargetState.Held) }));
        }
    }

    [Test]
    public void Build_NewestAttemptPerSystemDecides_WhenTheRowHasGone()
    {
        // Two attempts on one system: refused, then taken. The row is gone (delivered), so the newest child
        // Activity speaks, and it says set.
        var entry = Single(PasswordHistoryTimelineModel.Build(
        [
            Change(Now.AddMinutes(-30), outcomes:
            [
                Outcome(1, "Corporate Directory", ActivityStatus.FailedWithError, Now.AddMinutes(-29), "Connection refused"),
                Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now.AddMinutes(-20))
            ])
        ], [], Now));

        var target = entry.Targets.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.State, Is.EqualTo(PasswordHistoryTimelineModel.TargetState.Set));
            Assert.That(entry.Targets, Has.Count.EqualTo(1), "one chip per system, not per attempt");
        }
    }

    [Test]
    public void Build_FailedAttemptWithNoLiveRow_ReadsAsFailedInTheTargetsWords()
    {
        var entry = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now.AddDays(-2), outcomes: Outcome(1, "HR SQL", ActivityStatus.FailedWithError, Now.AddDays(-2), "The password does not meet the requirements of the domain."))],
            [], Now));

        var target = entry.Targets.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.State, Is.EqualTo(PasswordHistoryTimelineModel.TargetState.Failed));
            Assert.That(target.ChipSuffix, Is.EqualTo("failed"));
            Assert.That(target.PillModifier, Is.EqualTo("err"));
            Assert.That(target.Detail, Is.EqualTo("The password does not meet the requirements of the domain."));
        }
    }

    [TestCase(PendingPasswordChangeStatus.Parked, 3, false, "parked", "err")]
    [TestCase(PendingPasswordChangeStatus.Pending, 2, false, "retrying", "warn")]
    [TestCase(PendingPasswordChangeStatus.Pending, 0, true, "held", "warn")]
    [TestCase(PendingPasswordChangeStatus.Pending, 0, false, "queued", "neutral")]
    [TestCase(PendingPasswordChangeStatus.Delivering, 1, false, "delivering", "neutral")]
    [TestCase(PendingPasswordChangeStatus.Expired, 0, false, "expired", "err")]
    [TestCase(PendingPasswordChangeStatus.Cancelled, 0, false, "cancelled", "err")]
    public void Build_EachQueueState_HasItsChipSuffixAndPillColour(PendingPasswordChangeStatus status, int attempts, bool held, string suffix, string modifier)
    {
        var row = Row(1, "Corporate Directory", status, Now, attempts: attempts, nextRetryAt: attempts > 0 ? Now.AddMinutes(5) : null,
            taking: !held, origin: held ? PendingPasswordChangeOrigin.Propagated : PendingPasswordChangeOrigin.Explicit);

        var target = Single(PasswordHistoryTimelineModel.Build([Change(Now.AddMinutes(-1))], [row], Now)).Targets.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.ChipSuffix, Is.EqualTo(suffix));
            Assert.That(target.PillModifier, Is.EqualTo(modifier));
            Assert.That(target.Detail, Is.Not.Null, "anything other than a plain success says something beneath the chips");
        }
    }

    [Test]
    public void Build_ChipTooltip_CarriesTheTargetsWordsAndTheTime()
    {
        var at = Now.AddMinutes(-9);
        var entry = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now.AddMinutes(-10), outcomes: Outcome(1, "HR SQL", ActivityStatus.FailedWithError, at, "The password does not meet the requirements of the domain."))],
            [], Now));

        Assert.That(entry.Targets.Single().Tooltip,
            Is.EqualTo($"The password does not meet the requirements of the domain. Recorded {at.ToLocalTime().ToFriendlyDate()}."));
    }

    #endregion

    #region the words beneath

    [Test]
    public void Build_ParkedSystem_SaysWhyInTheTargetsWordsAndOffersRetry()
    {
        var target = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now.AddMinutes(-1))],
            [Row(4, "Corporate Directory", PendingPasswordChangeStatus.Parked, Now, PasswordSetFailureReason.Transient, "the LDAP server did not respond", attempts: 6)],
            Now)).Targets.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.Detail, Is.EqualTo("target unavailable, the LDAP server did not respond."));
            Assert.That(target.CanRetry, Is.True);
            Assert.That(target.CanStopTrying, Is.False);
            Assert.That(target.ConnectedSystemId, Is.EqualTo(4));
        }
    }

    [Test]
    public void Build_RetryingSystem_NamesTheNextAttemptAndOffersStopTrying()
    {
        var next = Now.AddMinutes(23);
        var target = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now.AddMinutes(-1))],
            [Row(4, "Corporate Directory", PendingPasswordChangeStatus.Pending, Now, PasswordSetFailureReason.Transient, "the LDAP server did not respond.", attempts: 2, nextRetryAt: next)],
            Now)).Targets.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.Detail, Is.EqualTo($"target unavailable, the LDAP server did not respond. Next attempt {next.ToLocalTime().ToFriendlyTime()}."),
                "the target's own full stop is not doubled, and a same-day next attempt is a time of day");
            Assert.That(target.CanStopTrying, Is.True);
            Assert.That(target.CanRetry, Is.False);
        }
    }

    [Test]
    public void Build_RetryingSystemWithNextAttemptOnAnotherDay_NamesTheDateToo()
    {
        var next = Now.AddDays(1);
        var target = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now.AddMinutes(-1))],
            [Row(4, "Corporate Directory", PendingPasswordChangeStatus.Pending, Now, attempts: 5, nextRetryAt: next)],
            Now)).Targets.Single();

        Assert.That(target.Detail, Is.EqualTo($"retrying. Next attempt {next.ToLocalTime().ToFriendlyDate()}."));
    }

    [Test]
    public void Build_CancelledSystem_NamesWhoCancelledIt()
    {
        var target = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now.AddMinutes(-1))],
            [Row(4, "Corporate Directory", PendingPasswordChangeStatus.Cancelled, Now, cancelledBy: "Grace Hopper")],
            Now)).Targets.Single();

        Assert.That(target.Detail, Is.EqualTo("cancelled by Grace Hopper."));
    }

    [Test]
    public void Build_SuccessDeliveredWithinAMinute_CarriesNoWords()
    {
        var target = Single(PasswordHistoryTimelineModel.Build(
            [Change(Now.AddMinutes(-10), outcomes: Outcome(1, "Corporate Directory", ActivityStatus.Complete, Now.AddMinutes(-10).AddSeconds(40)))],
            [], Now)).Targets.Single();

        Assert.That(target.Detail, Is.Null, "successes carry no words; the chip is the whole story");
    }

    [Test]
    public void Build_SuccessThatLaggedTheRequest_SaysWhenItLandedAndByHowMuch()
    {
        var requested = Now.AddMinutes(-30);
        var delivered = requested.AddMinutes(13);
        var target = Single(PasswordHistoryTimelineModel.Build(
            [Change(requested, outcomes: Outcome(1, "Corporate Directory", ActivityStatus.Complete, delivered))],
            [], Now)).Targets.Single();

        Assert.That(target.Detail, Is.EqualTo($"delivered {delivered.ToLocalTime().ToFriendlyTime()}, 13 min after the request."));
    }

    #endregion
}
