// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using Bunit;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the two shared affordances that keep a virtualised row to exactly one line: <see cref="OneLineText"/>
/// (a value, optionally with the secondary text that used to sit under it, clipped with an ellipsis) and
/// <see cref="OverflowList{TItem}"/> (the first item, with the rest behind a "+n more" dialog).
/// <para>
/// The virtualiser positions every row arithmetically from one fixed ItemSize, so a cell that wraps or stacks
/// drifts the scroll position, the row index in the URL and the reserved scroll space away from what is on
/// screen. What is pinned here is therefore structural: that the parts of a cell end up in ONE clamped element
/// rather than in two block elements, that only the first item of a list is rendered inline, and that whatever
/// is clipped is still reachable (the element's title, or the dialog).
/// </para>
/// <para>
/// Assertions are on JIM's own classes and components, never on MudBlazor's generated class names (see
/// test/CLAUDE.md > Blazor component tests).
/// </para>
/// </summary>
[TestFixture]
public class OneLineCellTests : JimComponentTestContext
{
    /// <summary>Renders an item as its own text, which is all these tests need to tell items apart.</summary>
    private static RenderFragment<string> TextTemplate =>
        item => builder => builder.AddContent(0, item);

    [Test]
    public void OneLineText_WithNoValue_RendersTheEmptyValuePlaceholder()
    {
        var cut = Render<OneLineText>(p => p.Add(c => c.Text, (string?)null));

        Assert.That(cut.HasComponent<EmptyValue>(), Is.True);
    }

    [Test]
    public void OneLineText_WithAValue_ClipsItToOneLine()
    {
        var cut = Render<OneLineText>(p => p.Add(c => c.Text, "a description long enough to need clipping"));

        var clamped = cut.Find(".jim-one-line");
        Assert.That(clamped.TextContent, Does.Contain("long enough to need clipping"));
    }

    /// <summary>
    /// The whole point of clipping: what is cut off must still be readable somewhere. The clamped element carries
    /// the full text as its title, so nothing is only visible when it happens to fit.
    /// </summary>
    [Test]
    public void OneLineText_WithAValue_KeepsTheFullTextAvailableInTheTitle()
    {
        const string full = "raw imported LDAP text that runs well past the width of any column it lands in";

        var cut = Render<OneLineText>(p => p.Add(c => c.Text, full));

        Assert.That(cut.Find(".jim-one-line").GetAttribute("title"), Is.EqualTo(full));
    }

    /// <summary>
    /// A MudText renders a paragraph, so secondary text under a value is a second line and a second row height.
    /// It has to end up inside the SAME clamped element as the value, not merely styled differently.
    /// </summary>
    [Test]
    public void OneLineText_WithSecondaryText_PutsItOnTheSameLineAsTheValue()
    {
        var cut = Render<OneLineText>(p => p
            .Add(c => c.Text, "Sync Interval")
            .Add(c => c.Secondary, "How often the scheduler polls"));

        var clamped = cut.Find(".jim-one-line");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clamped.TextContent, Does.Contain("Sync Interval"));
            Assert.That(clamped.TextContent, Does.Contain("How often the scheduler polls"));
            Assert.That(clamped.QuerySelector(".jim-one-line-secondary"), Is.Not.Null,
                "the secondary text stays visibly demoted rather than reading as part of the value");
            Assert.That(clamped.GetAttribute("title"), Does.Contain("How often the scheduler polls"),
                "the title carries both, since either half can be the part that is clipped");
        }
    }

    [Test]
    public void OneLineText_WithAnExplicitTooltip_UsesItInsteadOfTheValue()
    {
        var cut = Render<OneLineText>(p => p
            .Add(c => c.Text, "flattened message")
            .Add(c => c.Tooltip, "the whole message, capped"));

        Assert.That(cut.Find(".jim-one-line").GetAttribute("title"), Is.EqualTo("the whole message, capped"));
    }

    [Test]
    public void OverflowList_WithNoItems_RendersTheEmptyValuePlaceholder()
    {
        var cut = RenderOverflowList([]);

        Assert.That(cut.HasComponent<EmptyValue>(), Is.True);
    }

    [Test]
    public void OverflowList_WithNoItems_PrefersTheCallersOwnEmptyContent()
    {
        var cut = Render<OverflowList<string>>(p => p
            .Add(c => c.Items, new List<string>())
            .Add(c => c.ItemTemplate, TextTemplate)
            .Add(c => c.Title, "Roles")
            .Add(c => c.EmptyContent, (RenderFragment)(builder => builder.AddContent(0, "No roles"))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("No roles"));
            Assert.That(cut.HasComponent<EmptyValue>(), Is.False);
        }
    }

    [Test]
    public void OverflowList_WithOneItem_RendersItAloneWithNoAffordance()
    {
        var cut = RenderOverflowList(["Administrator"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Administrator"));
            Assert.That(cut.FindAll(".jim-attr-expand-link"), Is.Empty,
                "one item is the whole set, so there is nothing for an affordance to reveal");
        }
    }

    [Test]
    public void OverflowList_WithSeveralItems_RendersOnlyTheFirstInline()
    {
        var cut = RenderOverflowList(["Administrator", "Operator", "Auditor"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-one-line-list-value").TextContent, Does.Contain("Administrator"));
            Assert.That(cut.Markup, Does.Not.Contain("Operator"),
                "a second item rendered inline is a second line, whatever it is styled as");
            Assert.That(cut.Markup, Does.Not.Contain("Auditor"));
        }
    }

    [Test]
    public void OverflowList_WithSeveralItems_CountsWhatTheAffordanceWouldReveal()
    {
        var cut = RenderOverflowList(["Administrator", "Operator", "Auditor"]);

        Assert.That(cut.Find(".jim-attr-expand-link").TextContent.Trim(), Is.EqualTo("+2 more"));
    }

    /// <summary>
    /// Nothing may become unreachable: the items the row does not show are all in the dialog the affordance
    /// opens, rendered with the call site's own template rather than as bare text.
    /// </summary>
    [Test]
    public void OverflowList_Affordance_OpensADialogHoldingEveryItem()
    {
        var provider = Render<MudDialogProvider>();
        var cut = RenderOverflowList(["Administrator", "Operator", "Auditor"]);

        cut.Find(".jim-attr-expand-link").Click();

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(provider.Markup, Does.Contain("Administrator"));
                Assert.That(provider.Markup, Does.Contain("Operator"));
                Assert.That(provider.Markup, Does.Contain("Auditor"));
            }
        });
    }

    [Test]
    public void OverflowListDialog_StatesHowManyItemsItIsShowing()
    {
        var cut = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<OverflowListDialog<string>>
        {
            { x => x.Items, new List<string> { "Administrator", "Operator", "Auditor" } },
            { x => x.ItemTemplate, TextTemplate },
            { x => x.Title, "Roles" }
        };

        cut.InvokeAsync(() => dialogService.ShowAsync<OverflowListDialog<string>>("Roles", parameters));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("3 Roles")));
    }

    private IRenderedComponent<OverflowList<string>> RenderOverflowList(IReadOnlyList<string> items) =>
        Render<OverflowList<string>>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.ItemTemplate, TextTemplate)
            .Add(c => c.Title, "Roles"));
}
