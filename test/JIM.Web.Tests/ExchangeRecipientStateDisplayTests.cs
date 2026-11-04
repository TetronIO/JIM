// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The headline of an account's Exchange recipient panel (#2021, Phase 6): in step, a change being worked on, a change
/// waiting for a person, or declared but not yet read back by an import.
/// </summary>
[TestFixture]
public class ExchangeRecipientStateDisplayTests
{
    [Test]
    public void Headline_DeclaredIsWhatWasImported_IsInStep()
    {
        var (label, modifier) = ExchangeRecipientStateDisplay.Headline(State("Mailbox", "Mailbox", queued: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(label, Is.EqualTo("In step"));
            Assert.That(modifier, Is.EqualTo("ok"));
        }
    }

    [TestCase(ExchangeChangeState.Queued)]
    [TestCase(ExchangeChangeState.Delivering)]
    [TestCase(ExchangeChangeState.Retrying)]
    public void Headline_AChangeBeingWorkedOn_IsOutstanding(ExchangeChangeState state)
    {
        var (label, modifier) = ExchangeRecipientStateDisplay.Headline(State("Mailbox", null, Outcome(state, settled: false)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(label, Is.EqualTo("1 change outstanding"));
            Assert.That(modifier, Is.EqualTo("warn"));
        }
    }

    [TestCase(ExchangeChangeState.Parked, "err")]
    [TestCase(ExchangeChangeState.Refused, "err")]
    [TestCase(ExchangeChangeState.NotMade, "warn")]
    public void Headline_AChangeWaitingForAPerson_SaysWhereItStands(ExchangeChangeState state, string modifier)
    {
        var headline = ExchangeRecipientStateDisplay.Headline(State("Mailbox", null, Outcome(state, settled: true)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headline.Label, Is.EqualTo($"Change {ExchangeChangeOutcomeDisplay.Pill(state).Label.ToLowerInvariant()}"));
            Assert.That(headline.Modifier, Is.EqualTo(modifier));
        }
    }

    [Test]
    public void Headline_DeclaredButNotYetReadBackAndNothingQueued_AwaitsTheNextImport()
    {
        // Delivered, say, and the row removed: JIM believes Exchange only once an import shows it.
        var (label, modifier) = ExchangeRecipientStateDisplay.Headline(State("Mailbox", null, queued: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(label, Is.EqualTo("Awaiting import"));
            Assert.That(modifier, Is.EqualTo("neutral"));
        }
    }

    private static ExchangeRecipientState State(string? declared, string? imported, ExchangeChangeOutcome? queued) => new()
    {
        ConnectedSystemObjectId = Guid.NewGuid(),
        Declared = new ExchangeDeclaredState { RecipientType = declared },
        Imported = new ExchangeDeclaredState { RecipientType = imported },
        QueuedChange = queued
    };

    private static ExchangeChangeOutcome Outcome(ExchangeChangeState state, bool settled) =>
        new() { ActivityId = Guid.NewGuid(), State = state, RecipientType = "Mailbox", IsSettled = settled };
}
