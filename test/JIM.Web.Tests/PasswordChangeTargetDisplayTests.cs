// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How one target's delivery outcome reads in the Synchronise Password dialog (#1635): the words under the system's
/// name, and the icon and colour beside it. One helper rather than a switch in the dialog, so the Set Password
/// dialog's result stage can say the same things the same way when it moves onto the same outcomes.
/// </summary>
[TestFixture]
public class PasswordChangeTargetDisplayTests
{
    private static PasswordChangeTargetOutcome Outcome(
        PasswordChangeTargetState state,
        string? message = null,
        DateTime? nextAttemptAt = null) => new()
    {
        ConnectedSystemId = 3,
        ConnectedSystemName = "Corporate AD",
        State = state,
        Message = message,
        NextAttemptAt = nextAttemptAt
    };

    [Test]
    public void Words_Set_SaysThePasswordWasSet()
    {
        Assert.That(PasswordChangeTargetDisplay.Words(Outcome(PasswordChangeTargetState.Set, "Password set.")), Is.EqualTo("Password set"));
    }

    [Test]
    public void Words_Retrying_NamesTheNextAttemptAndTheLastAnswer()
    {
        var next = new DateTime(2026, 9, 5, 14, 30, 0, DateTimeKind.Utc);

        var words = PasswordChangeTargetDisplay.Words(Outcome(PasswordChangeTargetState.Retrying, "Connection refused", next));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(words, Does.StartWith("Retrying; next attempt at "));
            Assert.That(words, Does.Contain(next.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss")),
                "the time is shown the way every other timestamp in JIM is, in the viewer's local time");
            Assert.That(words, Does.EndWith("Last answer: Connection refused"));
        }
    }

    [Test]
    public void Words_RetryingWithNoTimeKnown_StillSaysItIsRetrying()
    {
        Assert.That(PasswordChangeTargetDisplay.Words(Outcome(PasswordChangeTargetState.Retrying)), Is.EqualTo("Retrying"));
    }

    [Test]
    public void Words_Parked_IsTheTargetsOwnWords()
    {
        // The target's message is where the remedy lives; JIM's framing would only bury it.
        Assert.That(PasswordChangeTargetDisplay.Words(Outcome(PasswordChangeTargetState.Parked, "The password does not meet the length, complexity or history requirements.")),
            Is.EqualTo("Parked: The password does not meet the length, complexity or history requirements."));
    }

    [Test]
    public void Words_ParkedWithNothingSaid_StillExplainsParked()
    {
        Assert.That(PasswordChangeTargetDisplay.Words(Outcome(PasswordChangeTargetState.Parked)),
            Is.EqualTo("Parked: JIM has stopped trying until somebody retries it"));
    }

    [TestCase(PasswordChangeTargetState.Queued, "Queued")]
    [TestCase(PasswordChangeTargetState.Delivering, "Delivering now")]
    [TestCase(PasswordChangeTargetState.Held, "Held until Password Synchronisation is switched on for this Connected System")]
    [TestCase(PasswordChangeTargetState.Expired, "Expired before it could be delivered")]
    [TestCase(PasswordChangeTargetState.Cancelled, "Cancelled")]
    public void Words_EveryOtherState_ReadsPlainly(PasswordChangeTargetState state, string expected)
    {
        Assert.That(PasswordChangeTargetDisplay.Words(Outcome(state)), Does.StartWith(expected));
    }

    [TestCase(PasswordChangeTargetState.Parked, true)]
    [TestCase(PasswordChangeTargetState.Expired, true)]
    [TestCase(PasswordChangeTargetState.Set, false)]
    [TestCase(PasswordChangeTargetState.Retrying, false)]
    [TestCase(PasswordChangeTargetState.Held, false)]
    [TestCase(PasswordChangeTargetState.Queued, false)]
    public void IsFailure_OnlyTheStatesNobodyIsGoingToFix(PasswordChangeTargetState state, bool expected)
    {
        // Retrying is not a failure: JIM is still on it. Parked and Expired are, because nothing will move without a person.
        Assert.That(PasswordChangeTargetDisplay.IsFailure(state), Is.EqualTo(expected));
    }

    [Test]
    public void Colour_SetIsSuccess_ParkedIsError_RetryingIsWarning()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PasswordChangeTargetDisplay.Colour(PasswordChangeTargetState.Set), Is.EqualTo(Color.Success));
            Assert.That(PasswordChangeTargetDisplay.Colour(PasswordChangeTargetState.Parked), Is.EqualTo(Color.Error));
            Assert.That(PasswordChangeTargetDisplay.Colour(PasswordChangeTargetState.Retrying), Is.EqualTo(Color.Warning));
            Assert.That(PasswordChangeTargetDisplay.Colour(PasswordChangeTargetState.Delivering), Is.EqualTo(Color.Info));
        }
    }

    [Test]
    public void Icon_EveryState_HasOne()
    {
        foreach (var state in Enum.GetValues<PasswordChangeTargetState>())
            Assert.That(PasswordChangeTargetDisplay.Icon(state), Is.Not.Empty, $"{state} has no icon");
    }
}
