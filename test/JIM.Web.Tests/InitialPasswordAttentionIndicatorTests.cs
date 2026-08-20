// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The needs-attention indicator on the Synchronisation Rules and Connected Systems lists (#1221 items 4 and 5).
/// <para>
/// The behaviour worth pinning is that parked and expired stay two separate chips. They ask for different
/// things: parked work is fixed by correcting the Synchronisation Rule's password settings and saving, expired
/// work cannot be fixed there at all. A future tidy-up that merged them into one total would read as a
/// simplification and would quietly merge "act here" with "act elsewhere".
/// </para>
/// </summary>
[TestFixture]
public class InitialPasswordAttentionIndicatorTests : JimComponentTestContext
{
    [Test]
    public void InitialPasswordAttentionIndicator_WithNothingOutstanding_RendersNothing()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p
            .Add(c => c.Attention, new InitialPasswordAttention()));

        Assert.That(cut.Markup.Trim(), Is.Empty,
            "silence is the reward for needing no action, matching the configuration drift indicator");
    }

    [Test]
    public void InitialPasswordAttentionIndicator_NotYetLoaded_RendersNothing()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p.Add(c => c.Attention, null));

        Assert.That(cut.Markup.Trim(), Is.Empty,
            "a list must not flash an indicator before the answer is known");
    }

    [Test]
    public void InitialPasswordAttentionIndicator_WithParkedAndExpired_RendersOneChipEach()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p
            .Add(c => c.Attention, new InitialPasswordAttention { ParkedCount = 14, ExpiredCount = 2 }));

        Assert.That(cut.FindComponents<MudBlazor.MudChip<string>>().Count, Is.EqualTo(2),
            "never one total: the two counts ask the administrator for different things");
    }

    [Test]
    public void InitialPasswordAttentionIndicator_WithParkedOnly_RendersOnlyTheParkedChip()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p
            .Add(c => c.Attention, new InitialPasswordAttention { ParkedCount = 3 }));

        var chips = cut.FindComponents<MudBlazor.MudChip<string>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chips.Count, Is.EqualTo(1));
            Assert.That(chips[0].Instance.Color, Is.EqualTo(MudBlazor.Color.Warning),
                "parked is fixable from the rule, so it is a warning rather than an error");
        }
    }

    [Test]
    public void InitialPasswordAttentionIndicator_WithExpiredOnly_RendersItAsAnError()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p
            .Add(c => c.Attention, new InitialPasswordAttention { ExpiredCount = 5 }));

        var chips = cut.FindComponents<MudBlazor.MudChip<string>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chips.Count, Is.EqualTo(1));
            Assert.That(chips[0].Instance.Color, Is.EqualTo(MudBlazor.Color.Error),
                "those accounts were provisioned and will never get a password from JIM; that is not a warning");
        }
    }

    [Test]
    public void InitialPasswordAttentionIndicator_WithLargeCounts_GroupsTheDigits()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p
            .Add(c => c.Attention, new InitialPasswordAttention { ParkedCount = 1400 }));

        Assert.That(cut.Markup, Does.Contain("1,400"));
    }

    [Test]
    public void InitialPasswordAttentionIndicator_OnASynchronisationRule_TooltipSaysSavingReleasesThem()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p
            .Add(c => c.Attention, new InitialPasswordAttention { ParkedCount = 14 })
            .Add(c => c.SubjectName, "Staff to Contoso AD"));

        var tooltip = TooltipTextOf(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooltip, Does.Contain("Staff to Contoso AD"),
                "naming the rule reads better than 'this one' when several rows carry the indicator");
            Assert.That(tooltip, Does.Contain("releases them"),
                "the tooltip has to say what to do about it, or the count is just a number");
        }
    }

    /// <summary>
    /// The same counts on a Connected System mean the settings to change live somewhere else, on the
    /// Synchronisation Rules that provisioned those accounts. Sending an administrator to the Connected System's
    /// own settings looking for a password generator would waste their time.
    /// </summary>
    [Test]
    public void InitialPasswordAttentionIndicator_OnAConnectedSystem_TooltipPointsAtTheRules()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p
            .Add(c => c.Attention, new InitialPasswordAttention { ParkedCount = 14 })
            .Add(c => c.SubjectName, "Contoso AD")
            .Add(c => c.IsConnectedSystem, true));

        Assert.That(TooltipTextOf(cut), Does.Contain("Synchronisation Rules that provisioned them"));
    }

    [Test]
    public void InitialPasswordAttentionIndicator_WithOneAccount_ReadsAsSingular()
    {
        var cut = Render<InitialPasswordAttentionIndicator>(p => p
            .Add(c => c.Attention, new InitialPasswordAttention { ParkedCount = 1 }));

        var tooltip = TooltipTextOf(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooltip, Does.Contain("1 account is"));
            Assert.That(tooltip, Does.Not.Contain("1 accounts"));
        }
    }

    /// <summary>
    /// Renders the first tooltip's own content fragment and returns its text.
    /// <para>
    /// MudBlazor renders tooltip content into a popover only once it is shown, so the words never reach the
    /// component's markup in a test. Rendering the fragment JIM supplied keeps the assertion on JIM's own output
    /// rather than on how MudBlazor happens to host a popover.
    /// </para>
    /// </summary>
    private string TooltipTextOf(IRenderedComponent<InitialPasswordAttentionIndicator> cut)
    {
        var content = cut.FindComponent<MudBlazor.MudTooltip>().Instance.TooltipContent;
        Assert.That(content, Is.Not.Null, "the indicator must explain itself, not just show a number");

        return Render(content!).Markup;
    }
}
