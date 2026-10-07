// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The banner on a Metaverse Object listing its generated values held for a decision (Unique Value Generation, #242,
/// release 4, Phase 9): one notice per value, the two actions only where the value waits on a decision, and the actions
/// handed to the page with the value they were pressed for.
/// </summary>
[TestFixture]
public class GeneratedValueDecisionBannerTests : JimComponentTestContext
{
    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private static GeneratedValueDecisionHeader Decision(string attribute, GeneratedValueDecisionStatus status) => new()
    {
        AssignmentId = Guid.NewGuid(),
        Status = status,
        AttributeName = attribute,
        Value = "r.okafor",
        Reason = GeneratedValueNeedsDecisionReason.AnchoredElsewhere,
        RejectedByConnectedSystemId = 3,
        RejectedByConnectedSystemName = "Contractor LDAP",
        AnchoredByConnectedSystemId = 2,
        AnchoredByConnectedSystemName = "Corporate AD"
    };

    [Test]
    public void Render_NothingHeld_RendersNothing()
    {
        var cut = Render<GeneratedValueDecisionBanner>(p => p.Add(c => c.Decisions, []));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void Render_AHeldValueAndAnAllowedRename_OneNoticeEachAndActionsOnlyOnTheHeldValue()
    {
        var cut = Render<GeneratedValueDecisionBanner>(p => p.Add(c => c.Decisions,
            [Decision("Account Name", GeneratedValueDecisionStatus.NeedsDecision), Decision("Email", GeneratedValueDecisionStatus.RenameAllowed)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudAlert>(), Has.Count.EqualTo(2));
            Assert.That(cut.FindAll("[data-testid='generated-value-allow-rename']"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll("[data-testid='generated-value-try-again']"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void TryAgain_Pressed_HandsThePageTheValueItWasPressedFor()
    {
        var held = Decision("Account Name", GeneratedValueDecisionStatus.NeedsDecision);
        GeneratedValueDecisionHeader? pressed = null;
        var cut = Render<GeneratedValueDecisionBanner>(p => p
            .Add(c => c.Decisions, [held])
            .Add(c => c.OnTryAgain, d => pressed = d));

        cut.Find("[data-testid='generated-value-try-again']").Click();

        Assert.That(pressed, Is.SameAs(held));
    }
}
