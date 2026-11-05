// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What a row on the Operations Exchange tab says about a queued change (#2021, Phase 6b): what it is waiting for, and why
/// it has not been made, in Exchange's own words where Exchange gave any.
/// </summary>
[TestFixture]
public class ExchangeQueueDisplayTests
{
    private static readonly DateTime LastAttempt = new(2026, 11, 3, 9, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime NextAttempt = new(2026, 11, 3, 9, 45, 0, DateTimeKind.Utc);

    [Test]
    public void QueueReason_FailedWithExchangesWords_GivesHowItFailedAndWhatExchangeSaid()
    {
        var reason = ExchangeChangeOutcomeDisplay.QueueReason(Change(PendingExchangeChangeStatus.Parked,
            ExchangeChangeFailureKind.InsufficientPermissions, "  Access is denied for Set-Mailbox.  "));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reason, Does.StartWith(ExchangeChangeOutcomeDisplay.Reason(ExchangeChangeFailureKind.InsufficientPermissions)));
            Assert.That(reason, Does.EndWith("Access is denied for Set-Mailbox."), "Exchange's words, trimmed");
        }
    }

    [Test]
    public void QueueReason_FailedWithoutWords_GivesHowItFailed()
    {
        var reason = ExchangeChangeOutcomeDisplay.QueueReason(Change(PendingExchangeChangeStatus.Pending, ExchangeChangeFailureKind.Unreachable, "   "));

        Assert.That(reason, Is.EqualTo(ExchangeChangeOutcomeDisplay.Reason(ExchangeChangeFailureKind.Unreachable)));
    }

    [Test]
    public void QueueReason_RefusedByJimBeforeAskingExchange_GivesJimsReason()
    {
        var reason = ExchangeChangeOutcomeDisplay.QueueReason(Change(PendingExchangeChangeStatus.Refused, null, "A mailbox cannot be created on a contact."));

        Assert.That(reason, Is.EqualTo("A mailbox cannot be created on a contact."));
    }

    [Test]
    public void QueueReason_NotFailed_IsNull()
    {
        Assert.That(ExchangeChangeOutcomeDisplay.QueueReason(Change(PendingExchangeChangeStatus.Pending, null, null)), Is.Null);
    }

    [Test]
    public void QueueDetail_Retrying_NamesTheAttemptAndWhenTheNextIsDue()
    {
        var change = Change(PendingExchangeChangeStatus.Pending, ExchangeChangeFailureKind.Unreachable, null, attempts: 3, next: NextAttempt);

        var detail = ExchangeChangeOutcomeDisplay.QueueDetail(change);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.State, Is.EqualTo(ExchangeChangeState.Retrying));
            Assert.That(detail, Does.Contain("3"));
            Assert.That(detail, Does.Contain(NextAttempt.ToLocalTime().ToFriendlyDate()));
        }
    }

    [Test]
    public void QueueDetail_RetryingWithNoBackoffLeft_SaysItIsDue()
    {
        var detail = ExchangeChangeOutcomeDisplay.QueueDetail(
            Change(PendingExchangeChangeStatus.Pending, ExchangeChangeFailureKind.Unreachable, null, attempts: 2, next: null));

        Assert.That(detail, Does.Contain("due").IgnoreCase);
    }

    [Test]
    public void QueueDetail_Parked_SaysSinceWhen()
    {
        var detail = ExchangeChangeOutcomeDisplay.QueueDetail(Change(PendingExchangeChangeStatus.Parked, ExchangeChangeFailureKind.Refused, null));

        Assert.That(detail, Does.Contain(LastAttempt.ToLocalTime().ToFriendlyDate()));
    }

    [TestCase(ActivityStatus.CompleteWithError, ExchangeChangeState.Refused)]
    [TestCase(ActivityStatus.CompleteWithWarning, ExchangeChangeState.NotMade)]
    public void QueueDetail_RefusedOrNotMade_HoldsUntilTheDeclarationChangesRatherThanNamingAnAttempt(ActivityStatus changeActivity, ExchangeChangeState expected)
    {
        var change = Change(PendingExchangeChangeStatus.Refused, null, "Not made.", attempts: 0);
        change.ChangeActivityStatus = changeActivity;
        change.LastAttemptedAt = null;

        var detail = ExchangeChangeOutcomeDisplay.QueueDetail(change);
        var retrying = ExchangeChangeOutcomeDisplay.QueueDetail(
            Change(PendingExchangeChangeStatus.Pending, ExchangeChangeFailureKind.Unreachable, null, attempts: 2, next: null));
        var parked = ExchangeChangeOutcomeDisplay.QueueDetail(Change(PendingExchangeChangeStatus.Parked, ExchangeChangeFailureKind.Refused, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.State, Is.EqualTo(expected));
            Assert.That(detail, Is.Not.EqualTo(retrying).And.Not.EqualTo(parked), "a held change does not read as one being attempted");
        }
    }

    [Test]
    public void QueueDetail_Held_SaysItWaitsForTheConnectedSystemToManageExchangeRecipientsAgain()
    {
        var held = Change(PendingExchangeChangeStatus.Pending, null, null, attempts: 0, manages: false);

        var detail = ExchangeChangeOutcomeDisplay.QueueDetail(held);
        var pill = ExchangeChangeOutcomeDisplay.Pill(held.State);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.State, Is.EqualTo(ExchangeChangeState.Held));
            Assert.That(detail, Does.Contain("Panoply AD").And.Contain("manages Exchange recipients"));
            Assert.That(pill.Running, Is.True, "a held change is still owed");
        }
    }

    private static PendingExchangeChangeHeader Change(PendingExchangeChangeStatus status, ExchangeChangeFailureKind? kind, string? message,
        int attempts = 1, DateTime? next = null, bool manages = true) => new()
    {
        ConnectedSystemManagesExchangeRecipients = manages,
        Id = Guid.NewGuid(),
        ConnectedSystemId = 3,
        ConnectedSystemName = "Panoply AD",
        RecipientType = "Mailbox",
        Status = status,
        FailureKind = kind,
        FailureMessage = message,
        AttemptCount = attempts,
        CreatedAt = LastAttempt.AddHours(-1),
        LastAttemptedAt = kind.HasValue ? LastAttempt : null,
        NextRetryAt = next,
        ChangeActivityStatus = ActivityStatus.Complete
    };
}
