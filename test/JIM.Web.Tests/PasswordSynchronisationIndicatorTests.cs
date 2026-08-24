// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Password Synchronisation indicator on the Connected Systems list (#1119, requirement 26).
/// <para>
/// Two things are worth pinning here. Parked and expired stay separate chips, for the reason
/// <see cref="InitialPasswordAttentionIndicator"/> keeps them apart: one is fixed by dealing with the cause and
/// retrying, the other cannot be fixed at all, so a single total would be a number with no action behind it.
/// And a system that is merely switched off still says so, because changes keep accumulating for it: silence
/// there would read as "nothing to see", when what it means is "a backlog is building".
/// </para>
/// </summary>
[TestFixture]
public class PasswordSynchronisationIndicatorTests : JimComponentTestContext
{
    [Test]
    public void PasswordSynchronisationIndicator_NotSupported_RendersNothing()
    {
        // Every Connected System whose Connector cannot set passwords would otherwise carry a chip saying so,
        // which on a list of them is a column of noise about a thing nobody can act on.
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.NotSupported));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void PasswordSynchronisationIndicator_NotConfigured_RendersNothing()
    {
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.NotConfigured));

        Assert.That(cut.Markup.Trim(), Is.Empty,
            "a system nobody has configured is the starting state of every system; it is not a finding");
    }

    [Test]
    public void PasswordSynchronisationIndicator_NotYetLoaded_RendersNothing()
    {
        var cut = Render<PasswordSynchronisationIndicator>(p => p.Add(c => c.State, null));

        Assert.That(cut.Markup.Trim(), Is.Empty,
            "a list must not flash an indicator before the answer is known");
    }

    [Test]
    public void PasswordSynchronisationIndicator_Enabled_RendersTheEnabledChip()
    {
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.Enabled));

        var chips = cut.FindComponents<MudBlazor.MudChip<string>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chips, Has.Exactly(1).Items);
            Assert.That(cut.Markup, Does.Contain("Password Sync"));
        }
    }

    [Test]
    public void PasswordSynchronisationIndicator_Disabled_SaysChangesAreStillAccumulating()
    {
        // The distinction this exists for: switching Password Synchronisation off does not discard the changes,
        // it queues them. An administrator who reads "off" as "nothing is happening" is wrong.
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.Disabled)
            .Add(c => c.ConnectedSystemName, "Contractor LDAP"));

        var tooltip = TooltipTextOf(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooltip, Does.Contain("accumulate"));
            Assert.That(tooltip, Does.Contain("Contractor LDAP"),
                "naming the system reads better than 'this one' when several rows carry the indicator");
        }
    }

    [Test]
    public void PasswordSynchronisationIndicator_WithParkedAndExpired_RendersOneChipEach()
    {
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.Enabled)
            .Add(c => c.Attention, new PasswordQueueAttention { ParkedCount = 14, ExpiredCount = 2 }));

        Assert.That(cut.FindComponents<MudBlazor.MudChip<string>>().Count, Is.EqualTo(3),
            "the state chip, plus one chip per count: never one total, because the two ask for different things");
    }

    [Test]
    public void PasswordSynchronisationIndicator_WithParkedOnly_RendersItAsAWarning()
    {
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.Enabled)
            .Add(c => c.Attention, new PasswordQueueAttention { ParkedCount = 3 }));

        var chips = cut.FindComponents<MudBlazor.MudChip<string>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chips.Count, Is.EqualTo(2));
            Assert.That(chips[1].Instance.Color, Is.EqualTo(MudBlazor.Color.Warning),
                "parked work can be retried once the cause is dealt with, so it is a warning rather than an error");
        }
    }

    [Test]
    public void PasswordSynchronisationIndicator_WithExpiredOnly_RendersItAsAnError()
    {
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.Enabled)
            .Add(c => c.Attention, new PasswordQueueAttention { ExpiredCount = 5 }));

        var chips = cut.FindComponents<MudBlazor.MudChip<string>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chips.Count, Is.EqualTo(2));
            Assert.That(chips[1].Instance.Color, Is.EqualTo(MudBlazor.Color.Error),
                "the password those changes carried is gone; that is not a warning");
        }
    }

    [Test]
    public void PasswordSynchronisationIndicator_WithLargeCounts_GroupsTheDigits()
    {
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.Enabled)
            .Add(c => c.Attention, new PasswordQueueAttention { ParkedCount = 1400 }));

        Assert.That(cut.Markup, Does.Contain("1,400"));
    }

    [Test]
    public void PasswordSynchronisationIndicator_NotSupportedButCarryingAttention_StillReportsIt()
    {
        // A Connector Definition can lose SupportsPasswordSet on upgrade while a queue of changes it once
        // accepted is still sitting there. Hiding those would leave work nothing on the page mentions.
        var cut = Render<PasswordSynchronisationIndicator>(p => p
            .Add(c => c.State, PasswordSynchronisationState.NotSupported)
            .Add(c => c.Attention, new PasswordQueueAttention { ParkedCount = 2 }));

        Assert.That(cut.FindComponents<MudBlazor.MudChip<string>>(), Has.Exactly(1).Items);
    }

    /// <summary>
    /// Renders the first tooltip's own content fragment and returns its text. MudBlazor renders tooltip content
    /// into a popover only once it is shown, so the words never reach the component's markup in a test.
    /// </summary>
    private string TooltipTextOf(IRenderedComponent<PasswordSynchronisationIndicator> cut)
    {
        var content = cut.FindComponent<MudBlazor.MudTooltip>().Instance.TooltipContent;
        Assert.That(content, Is.Not.Null, "the indicator must explain itself, not just show a state");

        return Render(content!).Markup;
    }
}
