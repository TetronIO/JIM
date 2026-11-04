// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// One row of the Exchange queue as the Operations tab, REST and PowerShell list it (#2021, Phase 6b): it reads as the
/// state the change's own follow-up shows, and offers Retry exactly where the row's own Retry would act.
/// </summary>
[TestFixture]
public class PendingExchangeChangeHeaderTests
{
    private static readonly DateTime At = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    [TestCase(PendingExchangeChangeStatus.Pending, false, ActivityStatus.Complete, ExchangeChangeState.Queued, false)]
    [TestCase(PendingExchangeChangeStatus.Pending, true, ActivityStatus.Complete, ExchangeChangeState.Retrying, true)]
    [TestCase(PendingExchangeChangeStatus.Delivering, false, ActivityStatus.Complete, ExchangeChangeState.Delivering, false)]
    [TestCase(PendingExchangeChangeStatus.Parked, true, ActivityStatus.Complete, ExchangeChangeState.Parked, true)]
    [TestCase(PendingExchangeChangeStatus.Refused, false, ActivityStatus.CompleteWithError, ExchangeChangeState.Refused, false)]
    [TestCase(PendingExchangeChangeStatus.Refused, false, ActivityStatus.CompleteWithWarning, ExchangeChangeState.NotMade, false)]
    public void State_EachRow_ReadsAsItsFollowUpDoes(PendingExchangeChangeStatus status, bool attempted, ActivityStatus changeActivityStatus,
        ExchangeChangeState expected, bool canRetry)
    {
        var header = new PendingExchangeChangeHeader
        {
            Status = status,
            LastAttemptedAt = attempted ? At : null,
            ChangeActivityStatus = changeActivityStatus
        };
        var row = new PendingExchangeChange { RecipientType = "Mailbox", Status = status, LastAttemptedAt = attempted ? At : null };
        var activity = new Activity { Id = Guid.NewGuid(), TargetType = ActivityTargetType.ExchangeRecipient, Status = changeActivityStatus };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.State, Is.EqualTo(expected));
            Assert.That(header.State, Is.EqualTo(ExchangeChangeOutcome.Describe(activity, row, []).State), "the list and the follow-up agree");
            Assert.That(header.CanRetry, Is.EqualTo(canRetry));
            Assert.That(header.CanRetry, Is.EqualTo(row.CanRetry), "the list offers Retry exactly where the row would act on it");
        }
    }
}
