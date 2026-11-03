// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Exchange;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// An account's Exchange recipient panel (#2021, Phase 6): which values it lists, and the queued change beneath them.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectExchangeRecipientPanelTests : JimComponentTestContext
{
    [Test]
    public void Render_MailboxWithAChangeQueued_ListsWhatIsDeclaredAndTheChange()
    {
        var cut = Render<ConnectedSystemObjectExchangeRecipientPanel>(p => p
            .Add(c => c.State, new ExchangeRecipientState
            {
                ConnectedSystemObjectId = Guid.NewGuid(),
                Declared = new ExchangeDeclaredState { RecipientType = "Mailbox", Alias = "dmorgan" },
                Imported = new ExchangeDeclaredState(),
                QueuedChange = new ExchangeChangeOutcome
                {
                    ActivityId = Guid.NewGuid(), State = ExchangeChangeState.Retrying, RecipientType = "Mailbox", OccurredAt = DateTime.UtcNow
                }
            })
            .Add(c => c.ConnectedSystemName, "PANOPLY"));

        var rows = cut.FindAll("tbody tr").Select(r => r.QuerySelector("td")!.TextContent).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Is.EqualTo(new[] { "Recipient type", "Alias" }),
                "the recipient type always, and only the other values something is declared or read for");
            Assert.That(cut.FindAll("[data-testid='jim-follow-up']"), Has.Count.EqualTo(1));
            Assert.That(cut.Find("[data-testid='jim-follow-up']").GetAttribute("data-state"), Is.EqualTo("Retrying"));
        }
    }

    [Test]
    public void Render_NothingQueued_ShowsNoChange()
    {
        var cut = Render<ConnectedSystemObjectExchangeRecipientPanel>(p => p
            .Add(c => c.State, new ExchangeRecipientState
            {
                ConnectedSystemObjectId = Guid.NewGuid(),
                Declared = new ExchangeDeclaredState { RecipientType = "Mailbox" },
                Imported = new ExchangeDeclaredState { RecipientType = "Mailbox" }
            }));

        Assert.That(cut.FindAll("[data-testid='jim-follow-up']"), Is.Empty);
    }
}
