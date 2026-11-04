// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How a follow-up of an execution item reads (#2668): its title, where it stands, the sentence beneath, and whether it
/// is still running. One model shared by the item page, the causality Lineage and the object timeline, so the three
/// cannot describe the same Initial Password in different words.
/// </summary>
[TestFixture]
public class ExecutionItemFollowUpDisplayTests
{
    [TestCase(PasswordChangeTargetState.Set, "Delivered", "ok", false)]
    [TestCase(PasswordChangeTargetState.Queued, "Queued", "neutral", true)]
    [TestCase(PasswordChangeTargetState.Delivering, "Delivering", "neutral", true)]
    [TestCase(PasswordChangeTargetState.Retrying, "Retrying", "warn", true)]
    [TestCase(PasswordChangeTargetState.Held, "Held", "warn", true)]
    [TestCase(PasswordChangeTargetState.Parked, "Parked", "err", false)]
    [TestCase(PasswordChangeTargetState.Expired, "Expired", "err", false)]
    [TestCase(PasswordChangeTargetState.Cancelled, "Cancelled", "err", false)]
    public void From_InitialPassword_ReadsItsStateFromItsTarget(PasswordChangeTargetState state, string label, string modifier, bool running)
    {
        var display = ExecutionItemFollowUpDisplay.From(InitialPassword(new PasswordChangeTargetOutcome { State = state, ConnectedSystemName = "PANOPLY" }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.Title, Is.EqualTo("Initial password"));
            Assert.That(display.StateLabel, Is.EqualTo(label));
            Assert.That(display.PillModifier, Is.EqualTo(modifier));
            Assert.That(display.IsRunning, Is.EqualTo(running));
            Assert.That(display.ActivityHref, Does.EndWith(display.ActivityId.ToString()));
        }
    }

    [Test]
    public void From_InitialPasswordSetWithAWarning_IsDeliveredInAmber()
    {
        var display = ExecutionItemFollowUpDisplay.From(InitialPassword(new PasswordChangeTargetOutcome
        {
            State = PasswordChangeTargetState.Set, CompletedWithWarning = true, Message = "Set; Password never expires is not supported here."
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.StateLabel, Is.EqualTo("Delivered"));
            Assert.That(display.PillModifier, Is.EqualTo("warn"));
            Assert.That(display.Detail, Does.Contain("never expires"));
        }
    }

    [Test]
    public void From_InitialPasswordCarriedByAWaitingChange_SaysSoInItsOwnWords()
    {
        // Carried: a password change already waiting for the account became its first password, so this follow-up has
        // no delivery of its own. Its Activity's message says which change carried it.
        var followUp = InitialPassword();
        followUp.Message = "A password change waiting for this person on PANOPLY will be delivered as the account's first password.";

        var display = ExecutionItemFollowUpDisplay.From(followUp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.StateLabel, Is.EqualTo("Carried"));
            Assert.That(display.PillModifier, Is.EqualTo("neutral"));
            Assert.That(display.Detail, Is.EqualTo(followUp.Message));
            Assert.That(display.IsRunning, Is.False, "its delivery is reported under the change that carries it");
        }
    }

    [Test]
    public void From_OtherKindOfFollowUp_ReadsFromItsActivity()
    {
        var display = ExecutionItemFollowUpDisplay.From(new ExecutionItemFollowUp
        {
            ActivityId = Guid.NewGuid(), TargetType = ActivityTargetType.ConnectedSystem, TargetName = "PANOPLY",
            Status = ActivityStatus.CompleteWithWarning, Message = "Something followed."
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.StateLabel, Is.EqualTo("Complete With Warning"));
            Assert.That(display.PillModifier, Is.EqualTo("warn"));
            Assert.That(display.Detail, Is.EqualTo("Something followed."));
        }
    }

    // ---- Exchange recipient changes (#2021, Phase 6) -------------------------------------------------------------

    [TestCase(ExchangeChangeState.Queued, "Queued", "neutral", true)]
    [TestCase(ExchangeChangeState.Delivering, "Delivering", "neutral", true)]
    [TestCase(ExchangeChangeState.Retrying, "Retrying", "warn", true)]
    [TestCase(ExchangeChangeState.Delivered, "Delivered", "ok", false)]
    [TestCase(ExchangeChangeState.Withdrawn, "Withdrawn", "ok", false)]
    [TestCase(ExchangeChangeState.Replaced, "Replaced", "neutral", false)]
    [TestCase(ExchangeChangeState.Parked, "Parked", "err", false)]
    [TestCase(ExchangeChangeState.Refused, "Refused", "err", false)]
    [TestCase(ExchangeChangeState.NotMade, "Not made", "warn", false)]
    public void From_ExchangeChange_ReadsItsStateFromItsOutcome(ExchangeChangeState state, string label, string modifier, bool running)
    {
        var display = ExecutionItemFollowUpDisplay.From(ExchangeChange(new ExchangeChangeOutcome { State = state, RecipientType = "Mailbox" }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.Kind, Is.EqualTo(ActivityTargetType.ExchangeRecipient));
            Assert.That(display.StateLabel, Is.EqualTo(label));
            Assert.That(display.PillModifier, Is.EqualTo(modifier));
            Assert.That(display.IsRunning, Is.EqualTo(running));
        }
    }

    [TestCase("Mailbox", "Mailbox")]
    [TestCase("SharedMailbox", "Shared mailbox")]
    [TestCase("EquipmentMailbox", "Equipment mailbox")]
    [TestCase("MailEnabledGroup", "Mail-enabled group")]
    [TestCase("None", "Exchange recipient")]
    [TestCase("Spaceship", "Exchange recipient")]
    public void From_ExchangeChange_IsTitledByTheRecipientItDeclares(string recipientType, string title)
    {
        var display = ExecutionItemFollowUpDisplay.From(ExchangeChange(new ExchangeChangeOutcome
        {
            State = ExchangeChangeState.Queued, RecipientType = recipientType
        }));

        Assert.That(display.Title, Is.EqualTo(title));
    }

    [Test]
    public void From_ExchangeChangeRetrying_SaysWhatWentWrongInExchangesWordsAndWhenItIsTriedNext()
    {
        var display = ExecutionItemFollowUpDisplay.From(ExchangeChange(new ExchangeChangeOutcome
        {
            State = ExchangeChangeState.Retrying, RecipientType = "Mailbox", AttemptCount = 2,
            FailureKind = ExchangeChangeFailureKind.Unreachable, Message = "Exchange did not answer within 60 seconds.",
            NextAttemptAt = DateTime.UtcNow.AddMinutes(4)
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.Detail, Does.Contain("Exchange did not answer within 60 seconds."));
            Assert.That(display.Detail, Does.Contain("Attempt 2"));
            Assert.That(display.Detail, Does.Contain("Next attempt"));
        }
    }

    [Test]
    public void From_ExchangeChangeRefused_GivesJimsReason()
    {
        var display = ExecutionItemFollowUpDisplay.From(ExchangeChange(new ExchangeChangeOutcome
        {
            State = ExchangeChangeState.Refused, RecipientType = "Mailbox", Message = "A group cannot be a mailbox."
        }));

        Assert.That(display.Detail, Does.Contain("A group cannot be a mailbox."));
    }

    [Test]
    public void From_ExchangeChangeWithNoOutcome_ReadsFromItsActivityUnderItsOwnName()
    {
        // No outcome is read where the change's own Activity could not be matched, so the Activity speaks for it.
        var followUp = ExchangeChange(outcome: null);

        var display = ExecutionItemFollowUpDisplay.From(followUp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.Title, Is.EqualTo("Exchange recipient"));
            Assert.That(display.StateLabel, Is.EqualTo("Complete"));
            Assert.That(display.Detail, Is.EqualTo(followUp.Message));
        }
    }

    [Test]
    public void From_InitialPassword_IsAPasswordFollowUp()
    {
        var display = ExecutionItemFollowUpDisplay.From(InitialPassword(new PasswordChangeTargetOutcome { State = PasswordChangeTargetState.Set }));

        Assert.That(display.Kind, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
    }

    private static ExecutionItemFollowUp ExchangeChange(ExchangeChangeOutcome? outcome) => new()
    {
        ActivityId = Guid.NewGuid(),
        RunProfileExecutionItemId = Guid.NewGuid(),
        TargetType = ActivityTargetType.ExchangeRecipient,
        TargetName = "PANOPLY",
        Status = ActivityStatus.Complete,
        Message = "Mailbox queued for Exchange on PANOPLY.",
        ExchangeOutcome = outcome
    };

    private static ExecutionItemFollowUp InitialPassword(PasswordChangeTargetOutcome? target = null) => new()
    {
        ActivityId = Guid.NewGuid(),
        RunProfileExecutionItemId = Guid.NewGuid(),
        TargetType = ActivityTargetType.PasswordSynchronisation,
        TargetName = "PANOPLY",
        Status = ActivityStatus.Complete,
        Message = "Initial password queued for delivery to PANOPLY.",
        PasswordOutcomes = new PasswordChangeOutcomes { Targets = target == null ? [] : [target] }
    };
}
