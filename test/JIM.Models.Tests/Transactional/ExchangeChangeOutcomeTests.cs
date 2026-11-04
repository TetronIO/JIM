// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Activities;
using JIM.Models.Exchange;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// Where a queued Exchange change stands (#2021, Phase 6): the queue row while it is on the queue, then the Activities
/// recorded under it once it has left.
/// </summary>
[TestFixture]
public class ExchangeChangeOutcomeTests
{
    private static readonly DateTime Queued = new(2026, 11, 3, 10, 2, 14, DateTimeKind.Utc);
    private static readonly Guid ChangeActivityId = Guid.NewGuid();

    // ---- On the queue: the row describes it ---------------------------------------------------------------------

    [Test]
    public void Describe_RowNotYetAttempted_IsQueuedAndStillRunning()
    {
        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), Row(PendingExchangeChangeStatus.Pending), []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.ActivityId, Is.EqualTo(ChangeActivityId));
            Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.Queued));
            Assert.That(outcome.IsSettled, Is.False);
            Assert.That(outcome.AttemptCount, Is.Zero);
            Assert.That(outcome.RecipientType, Is.EqualTo(ExchangeRecipientTypes.Mailbox));
            Assert.That(outcome.OccurredAt, Is.EqualTo(Queued));
            Assert.That(outcome.NextAttemptAt, Is.Null);
        }
    }

    [Test]
    public void Describe_RowWaitingOutABackoff_IsRetryingWithItsNextAttemptAndExchangesWords()
    {
        var row = Row(PendingExchangeChangeStatus.Pending);
        row.AttemptCount = 2;
        row.LastAttemptedAt = Queued.AddMinutes(2);
        row.NextRetryAt = Queued.AddMinutes(6);
        row.FailureKind = ExchangeChangeFailureKind.Unreachable;
        row.FailureMessage = "Exchange did not answer within 60 seconds.";

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), row, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.Retrying));
            Assert.That(outcome.IsSettled, Is.False);
            Assert.That(outcome.AttemptCount, Is.EqualTo(2));
            Assert.That(outcome.NextAttemptAt, Is.EqualTo(Queued.AddMinutes(6)));
            Assert.That(outcome.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
            Assert.That(outcome.Message, Is.EqualTo("Exchange did not answer within 60 seconds."));
            Assert.That(outcome.OccurredAt, Is.EqualTo(Queued.AddMinutes(2)));
        }
    }

    [TestCase(PendingExchangeChangeStatus.Delivering, ExchangeChangeState.Delivering, false)]
    [TestCase(PendingExchangeChangeStatus.Parked, ExchangeChangeState.Parked, true)]
    public void Describe_RowInAState_ReadsAsThatState(PendingExchangeChangeStatus status, ExchangeChangeState expected, bool settled)
    {
        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), Row(status), []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.State, Is.EqualTo(expected));
            Assert.That(outcome.IsSettled, Is.EqualTo(settled));
        }
    }

    [Test]
    public void Describe_RefusedRow_IsRefusedWithJimsReason()
    {
        var row = Row(PendingExchangeChangeStatus.Refused);
        row.FailureMessage = "A group cannot be a mailbox.";

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(ActivityStatus.CompleteWithError), row, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.Refused));
            Assert.That(outcome.IsSettled, Is.True);
            Assert.That(outcome.Message, Is.EqualTo("A group cannot be a mailbox."));
        }
    }

    [Test]
    public void Describe_RefusedRowWhoseChangeWasATypeChange_IsNotMade()
    {
        // Staging records a type change this release does not make as a refused row under a warning, not an error.
        var row = Row(PendingExchangeChangeStatus.Refused);
        row.FailureMessage = "Mailbox to SharedMailbox is a type change, which this release does not make.";

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(ActivityStatus.CompleteWithWarning), row, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.NotMade));
            Assert.That(outcome.IsSettled, Is.True);
            Assert.That(outcome.Message, Does.Contain("type change"));
        }
    }

    [Test]
    public void Describe_RowPresent_TakesPrecedenceOverEarlierOutcomes()
    {
        // A failure was recorded, then an administrator retried it: the row is queued again and says so.
        var events = new List<ExchangeChangeEvent>
        {
            Event(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.CompleteWithError, Queued.AddMinutes(2))
        };

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), Row(PendingExchangeChangeStatus.Pending), events);

        Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.Queued));
    }

    // ---- Off the queue: the newest outcome describes it ---------------------------------------------------------

    [Test]
    public void Describe_NoRowAndADelivery_IsDeliveredAsOfThatDelivery()
    {
        var events = new List<ExchangeChangeEvent>
        {
            Event(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.CompleteWithError, Queued.AddMinutes(2), error: "Timed out."),
            Event(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.Complete, Queued.AddMinutes(6), message: "Exchange recipient set: Mailbox.")
        };

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), row: null, events);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.Delivered));
            Assert.That(outcome.IsSettled, Is.True);
            Assert.That(outcome.Message, Is.EqualTo("Exchange recipient set: Mailbox."));
            Assert.That(outcome.OccurredAt, Is.EqualTo(Queued.AddMinutes(6)));
            Assert.That(outcome.AttemptCount, Is.Null, "the attempts left the queue with the row");
            Assert.That(outcome.RecipientType, Is.EqualTo(ExchangeRecipientTypes.Mailbox), "read from the change's Activity once the row is gone");
        }
    }

    [TestCase(ActivityTargetOperationType.WithdrawExchangeRecipient, ExchangeChangeState.Withdrawn)]
    [TestCase(ActivityTargetOperationType.ReplaceExchangeRecipient, ExchangeChangeState.Replaced)]
    public void Describe_NoRowAndAnOutcome_ReadsAsWhatThatOutcomeWas(ActivityTargetOperationType operation, ExchangeChangeState expected)
    {
        var events = new List<ExchangeChangeEvent> { Event(operation, ActivityStatus.Complete, Queued.AddMinutes(1), message: "said so") };

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), row: null, events);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.State, Is.EqualTo(expected));
            Assert.That(outcome.IsSettled, Is.True);
            Assert.That(outcome.Message, Is.EqualTo("said so"));
        }
    }

    [Test]
    public void Describe_NoRowAndAFailureLast_IsParked()
    {
        // A parked row is removed by retention; the last thing known about the change is the failure that parked it.
        var events = new List<ExchangeChangeEvent>
        {
            Event(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.CompleteWithError, Queued.AddMinutes(2),
                message: "Exchange recipient not set", error: "Exchange refused the change: the alias is in use.")
        };

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), row: null, events);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.Parked));
            Assert.That(outcome.Message, Is.EqualTo("Exchange refused the change: the alias is in use."), "the error, which says why");
        }
    }

    [Test]
    public void Describe_NoRowAndARetryLast_IsReadFromTheOutcomeBeforeIt()
    {
        // A retry is an administrator's request, not an outcome: what the change came to is what was recorded before it.
        var events = new List<ExchangeChangeEvent>
        {
            Event(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.Complete, Queued.AddMinutes(2)),
            Event(ActivityTargetOperationType.RetryExchangeRecipient, ActivityStatus.Complete, Queued.AddMinutes(3))
        };

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), row: null, events);

        Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.Delivered));
    }

    [TestCase(ActivityStatus.CompleteWithError, ExchangeChangeState.Refused)]
    [TestCase(ActivityStatus.CompleteWithWarning, ExchangeChangeState.NotMade)]
    [TestCase(ActivityStatus.Complete, ExchangeChangeState.Withdrawn)]
    public void Describe_NoRowAndNoOutcome_ReadsFromTheChangesOwnActivity(ActivityStatus status, ExchangeChangeState expected)
    {
        var activity = ChangeActivity(status);
        activity.Message = "what staging said";
        activity.ErrorMessage = status == ActivityStatus.CompleteWithError ? "A group cannot be a mailbox." : null;

        var outcome = ExchangeChangeOutcome.Describe(activity, row: null, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.State, Is.EqualTo(expected));
            Assert.That(outcome.IsSettled, Is.True);
            Assert.That(outcome.OccurredAt, Is.EqualTo(Queued));
            Assert.That(outcome.Message, Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public void Describe_ActivityWithNoRecipientType_ReadsAsNone()
    {
        var activity = ChangeActivity();
        activity.TargetContext = null;

        var outcome = ExchangeChangeOutcome.Describe(activity, row: null,
            [Event(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.Complete, Queued)]);

        Assert.That(outcome.RecipientType, Is.EqualTo(ExchangeRecipientTypes.None));
    }

    [Test]
    public void Describe_EventsForAnotherChange_AreIgnored()
    {
        var other = Event(ActivityTargetOperationType.WithdrawExchangeRecipient, ActivityStatus.Complete, Queued.AddMinutes(5));
        other.ChangeActivityId = Guid.NewGuid();

        var outcome = ExchangeChangeOutcome.Describe(ChangeActivity(), Row(PendingExchangeChangeStatus.Pending), [other]);

        Assert.That(outcome.State, Is.EqualTo(ExchangeChangeState.Queued));
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static Activity ChangeActivity(ActivityStatus status = ActivityStatus.Complete) => new()
    {
        Id = ChangeActivityId,
        TargetType = ActivityTargetType.ExchangeRecipient,
        TargetOperationType = ActivityTargetOperationType.SetExchangeRecipient,
        TargetContext = ExchangeRecipientTypes.Mailbox,
        TargetName = "PANOPLY",
        Status = status,
        Message = "Mailbox queued for Exchange.",
        Created = Queued
    };

    private static PendingExchangeChange Row(PendingExchangeChangeStatus status) => new()
    {
        ConnectedSystemId = 1,
        ConnectedSystemObjectId = Guid.NewGuid(),
        RecipientType = ExchangeRecipientTypes.Mailbox,
        Alias = "dmorgan",
        Status = status,
        CreatedAt = Queued,
        ActivityId = ChangeActivityId
    };

    private static ExchangeChangeEvent Event(ActivityTargetOperationType operation, ActivityStatus status, DateTime at,
        string? message = null, string? error = null) => new()
    {
        ChangeActivityId = ChangeActivityId,
        ActivityId = Guid.NewGuid(),
        Operation = operation,
        Status = status,
        Message = message,
        ErrorMessage = error,
        OccurredAt = at
    };
}
