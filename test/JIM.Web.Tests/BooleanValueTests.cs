// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers BooleanValue, the single rendering of a Boolean attribute value. The label beside the icon is
/// the point of the component: the Connector Space table rendered the icon alone, where a cross reads
/// almost identically to an attribute that holds no value at all, while the three Metaverse Object views
/// each carried their own copy of the icon-plus-label markup. One component, one answer.
/// </summary>
[TestFixture]
public class BooleanValueTests : JimComponentTestContext
{
    [Test]
    public void BooleanValue_True_RendersCheckIconAndLabel()
    {
        var cut = Render<BooleanValue>(p => p.Add(c => c.Value, true));
        var icon = cut.FindComponent<MudIcon>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(icon.Icon, Is.EqualTo(Icons.Material.Filled.Check));
            Assert.That(icon.Color, Is.EqualTo(Color.Success));
            Assert.That(cut.Markup, Does.Contain("True"));
        }
    }

    [Test]
    public void BooleanValue_False_RendersCloseIconAndLabel()
    {
        var cut = Render<BooleanValue>(p => p.Add(c => c.Value, false));
        var icon = cut.FindComponent<MudIcon>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(icon.Icon, Is.EqualTo(Icons.Material.Filled.Close));
            Assert.That(icon.Color, Is.EqualTo(Color.Default));
            Assert.That(cut.Markup, Does.Contain("False"));
        }
    }

    [Test]
    public void BooleanValue_False_DoesNotRenderTheOppositeLabel()
    {
        // "False" contains no substring of "True", so the pair of assertions genuinely tells the two states
        // apart; without this a component rendering both labels would pass the two tests above.
        var cut = Render<BooleanValue>(p => p.Add(c => c.Value, false));

        Assert.That(cut.Markup, Does.Not.Contain("True"));
    }

    [Test]
    public void BooleanValue_UsesDefaultIconSize()
    {
        // JIM.Web/CLAUDE.md: default sizing unless the user asks for otherwise. The markup this component
        // replaced specified Size.Small in three of its four copies.
        var cut = Render<BooleanValue>(p => p.Add(c => c.Value, true));

        Assert.That(cut.FindComponent<MudIcon>().Instance.Size, Is.EqualTo(Size.Medium));
    }
}
