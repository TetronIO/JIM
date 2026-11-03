// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How a run's follow-up summary reads (#2021, Phase 6): one line per kind of work, its states in a fixed order with
/// the tone each follow-up of that state would show, and whether anything is still running or stuck, which decides the
/// tone of the Activity list's chip.
/// </summary>
[TestFixture]
public class FollowUpSummaryDisplayTests
{
    [Test]
    public void From_CountsOfBothKinds_IsOneLinePerKindPasswordsFirst()
    {
        var lines = FollowUpSummaryDisplay.From(
        [
            Count(ActivityTargetType.ExchangeRecipient, "Delivered", 10),
            Count(ActivityTargetType.PasswordSynchronisation, "Set", 12)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines.Select(l => l.Kind), Is.EqualTo(new[] { ActivityTargetType.PasswordSynchronisation, ActivityTargetType.ExchangeRecipient }));
            Assert.That(lines.Select(l => l.Total), Is.EqualTo(new[] { 12, 10 }));
        }
    }

    [Test]
    public void From_ExchangeStates_AreOrderedSettledFirstThenRunningThenStuck()
    {
        var line = FollowUpSummaryDisplay.From(
        [
            Count(ActivityTargetType.ExchangeRecipient, "Parked", 1),
            Count(ActivityTargetType.ExchangeRecipient, "Retrying", 1),
            Count(ActivityTargetType.ExchangeRecipient, "Delivered", 10)
        ]).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(line.States.Select(s => s.Label), Is.EqualTo(new[] { "delivered", "retrying", "parked" }));
            Assert.That(line.States.Select(s => s.Modifier), Is.EqualTo(new[] { "ok", "warn", "err" }));
            Assert.That(line.States.Select(s => s.Count), Is.EqualTo(new[] { 10, 1, 1 }));
            Assert.That(line.RunningCount, Is.EqualTo(1));
            Assert.That(line.StuckCount, Is.EqualTo(1));
        }
    }

    [TestCase("Set", "delivered", "ok", false, false)]
    [TestCase("Queued", "queued", "neutral", true, false)]
    [TestCase("Retrying", "retrying", "warn", true, false)]
    [TestCase("Parked", "parked", "err", false, true)]
    [TestCase("Expired", "expired", "err", false, true)]
    [TestCase("Carried", "carried", "neutral", false, false)]
    public void From_PasswordState_ReadsAsItsFollowUpWould(string state, string label, string modifier, bool running, bool stuck)
    {
        var line = FollowUpSummaryDisplay.From([Count(ActivityTargetType.PasswordSynchronisation, state, 3)]).Single();
        var shown = line.States.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shown.Label, Is.EqualTo(label));
            Assert.That(shown.Modifier, Is.EqualTo(modifier));
            Assert.That(line.RunningCount, Is.EqualTo(running ? 3 : 0));
            Assert.That(line.StuckCount, Is.EqualTo(stuck ? 3 : 0));
        }
    }

    [TestCase("NotMade", "not made", "warn", false, true)]
    [TestCase("Refused", "refused", "err", false, true)]
    [TestCase("Withdrawn", "withdrawn", "ok", false, false)]
    [TestCase("Replaced", "replaced", "neutral", false, false)]
    public void From_ExchangeState_ReadsAsItsFollowUpWould(string state, string label, string modifier, bool running, bool stuck)
    {
        var line = FollowUpSummaryDisplay.From([Count(ActivityTargetType.ExchangeRecipient, state, 2)]).Single();
        var shown = line.States.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shown.Label, Is.EqualTo(label));
            Assert.That(shown.Modifier, Is.EqualTo(modifier));
            Assert.That(line.RunningCount, Is.EqualTo(running ? 2 : 0));
            Assert.That(line.StuckCount, Is.EqualTo(stuck ? 2 : 0));
        }
    }

    [Test]
    public void From_AStateItDoesNotRecognise_IsShownAsItCameRatherThanDropped()
    {
        var line = FollowUpSummaryDisplay.From([Count(ActivityTargetType.ExchangeRecipient, "Teleported", 1)]).Single();

        Assert.That(line.States.Single().Count, Is.EqualTo(1));
    }

    [Test]
    public void From_NoCounts_IsNothing()
    {
        Assert.That(FollowUpSummaryDisplay.From([]), Is.Empty);
    }

    private static FollowUpStateCount Count(ActivityTargetType kind, string state, int count) =>
        new() { RunActivityId = Guid.Empty, Kind = kind, State = state, Count = count };
}
