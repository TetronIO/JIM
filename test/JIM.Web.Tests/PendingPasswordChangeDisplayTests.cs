// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How a queued password change reads on screen (#1119).
/// <para>
/// This exists as one shared helper because the queue page and the Metaverse Object's panel show the same rows.
/// Written separately in each, they drifted immediately: one named the failure reason before the target's
/// message and the other showed the message alone, so the same parked change read as two different problems
/// depending on which page an administrator opened.
/// </para>
/// </summary>
[TestFixture]
public class PendingPasswordChangeDisplayTests
{
    private static PendingPasswordChangeHeader Change(
        PendingPasswordChangeStatus status = PendingPasswordChangeStatus.Pending,
        PasswordSetFailureReason? reason = null,
        string? targetMessage = null,
        string? cancelledByName = null,
        bool takingPasswords = true) => new()
    {
        Status = status,
        FailureReason = reason,
        TargetMessage = targetMessage,
        CancelledByName = cancelledByName,
        ConnectedSystemTakingPasswords = takingPasswords
    };

    [Test]
    public void Status_Pending_ReadsAsWaiting()
    {
        // Pending is the storage name and says nothing about whether anything is happening.
        Assert.That(PendingPasswordChangeDisplay.Status(Change()), Is.EqualTo("Waiting"));
    }

    [TestCase(PendingPasswordChangeStatus.Parked, "Parked")]
    [TestCase(PendingPasswordChangeStatus.Expired, "Expired")]
    [TestCase(PendingPasswordChangeStatus.Cancelled, "Cancelled")]
    public void Status_EveryOtherState_ReadsAsItself(PendingPasswordChangeStatus status, string expected)
    {
        Assert.That(PendingPasswordChangeDisplay.Status(Change(status)), Is.EqualTo(expected));
    }

    [Test]
    public void Detail_NamesTheReasonBeforeTheTargetsOwnWords()
    {
        // Both halves earn their place: the message is the target speaking, which is where the remedy usually
        // lives; the reason is JIM's classification, which decides whether another attempt could ever help.
        var detail = PendingPasswordChangeDisplay.Detail(Change(
            PendingPasswordChangeStatus.Parked,
            PasswordSetFailureReason.PolicyRejection,
            "password does not meet complexity requirements"));

        Assert.That(detail, Is.EqualTo("Policy rejection: password does not meet complexity requirements"));
    }

    [Test]
    public void Detail_NoTargetMessage_FallsBackToTheReasonAlone()
    {
        var detail = PendingPasswordChangeDisplay.Detail(Change(
            PendingPasswordChangeStatus.Parked, PasswordSetFailureReason.Transient));

        Assert.That(detail, Is.EqualTo("Target unavailable"));
    }

    [Test]
    public void Detail_NeverAttempted_SaysNothing()
    {
        Assert.That(PendingPasswordChangeDisplay.Detail(Change()), Is.Null,
            "a change that has not been tried has no reason, and inventing one would be a claim about the target");
    }

    [Test]
    public void Detail_FailureReasonNone_SaysNothing()
    {
        Assert.That(PendingPasswordChangeDisplay.Detail(
            Change(reason: PasswordSetFailureReason.None)), Is.Null);
    }

    [Test]
    public void Detail_Cancelled_NamesWhoCancelledIt()
    {
        var detail = PendingPasswordChangeDisplay.Detail(Change(
            PendingPasswordChangeStatus.Cancelled, cancelledByName: "Alex Admin"));

        Assert.That(detail, Is.EqualTo("Cancelled by Alex Admin"));
    }

    [Test]
    public void Detail_CancelledWithAnApiKey_StillSaysAnAdministratorDidIt()
    {
        // A cancellation made with an API key has no person behind it, but somebody still chose to make it, and
        // silence there would read as though the change stopped on its own.
        Assert.That(PendingPasswordChangeDisplay.Detail(Change(PendingPasswordChangeStatus.Cancelled)),
            Is.EqualTo("Cancelled by an administrator"));
    }

    /// <summary>
    /// A cancelled change can carry the failure that parked it before somebody gave up on it. The cancellation is
    /// what happened to it, and is what the row should say.
    /// </summary>
    [Test]
    public void Detail_CancelledAfterAFailure_ReportsTheCancellationRatherThanTheFailure()
    {
        var detail = PendingPasswordChangeDisplay.Detail(Change(
            PendingPasswordChangeStatus.Cancelled,
            PasswordSetFailureReason.PolicyRejection,
            "password does not meet complexity requirements",
            "Alex Admin"));

        Assert.That(detail, Is.EqualTo("Cancelled by Alex Admin"));
    }

    [Test]
    public void Reason_EveryValue_ReadsAsWordsRatherThanTheEnumSpelling()
    {
        foreach (var reason in Enum.GetValues<PasswordSetFailureReason>())
        {
            var text = PendingPasswordChangeDisplay.Reason(reason);

            if (reason == PasswordSetFailureReason.None)
                continue;

            Assert.That(text, Does.Not.EqualTo(reason.ToString()),
                $"{reason} falls through to its enum spelling; a new failure reason needs its own words here");
        }
    }

    /// <summary>
    /// A change queued for a Connected System that is switched off is Pending with no failure against it, so
    /// without this it reads as "Waiting" and nothing else, which is the one state where what it is waiting for
    /// is a person rather than a retry. Nothing else on the row says so.
    /// </summary>
    [Test]
    public void Detail_HeldBehindASwitchedOffSystem_SaysWhatItIsWaitingFor()
    {
        var detail = PendingPasswordChangeDisplay.Detail(Change(takingPasswords: false));

        Assert.That(detail, Is.EqualTo(
            "Waiting for Password Synchronisation to be switched on for this Connected System"));
    }

    /// <summary>
    /// A system switched off after a delivery attempt already failed carries both facts, and both matter: the
    /// failure is what an administrator has to fix before switching the system back on is worth doing.
    /// </summary>
    [Test]
    public void Detail_HeldAfterAFailedAttempt_KeepsTheFailureToo()
    {
        var detail = PendingPasswordChangeDisplay.Detail(Change(
            reason: PasswordSetFailureReason.PolicyRejection,
            targetMessage: "password does not meet complexity requirements",
            takingPasswords: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail, Does.StartWith("Waiting for Password Synchronisation to be switched on"));
            Assert.That(detail, Does.Contain("password does not meet complexity requirements"));
        }
    }

    [Test]
    public void IsHeld_OnlyWhilePendingAndTheSystemIsOff()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Change(takingPasswords: false).IsHeld, Is.True);
            Assert.That(Change().IsHeld, Is.False, "a live system's change is on its way, not held");
            Assert.That(Change(PendingPasswordChangeStatus.Parked, takingPasswords: false).IsHeld, Is.False,
                "a parked change waits on the reason it was refused, whatever the system's enabled state");
            Assert.That(Change(PendingPasswordChangeStatus.Expired, takingPasswords: false).IsHeld, Is.False,
                "an expired change carries no password to hold");
        }
    }

    /// <summary>
    /// The reading the queue page depends on: a held change must not show "Due now" beside a summary that
    /// correctly counts it as waiting and not due. A delivery pass steps over its system without reaching it,
    /// whatever the retry time says.
    /// </summary>
    [Test]
    public void IsDue_HeldBehindASwitchedOffSystem_IsFalse()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Change(takingPasswords: false).IsDue(now), Is.False);
            Assert.That(Change().IsDue(now), Is.True);
        }
    }
}
