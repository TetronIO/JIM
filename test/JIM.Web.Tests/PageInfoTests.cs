// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="PageInfo"/>: the info button in a page's title bar that says what the page
/// is for, which is the design system's one way of describing a page.
/// </summary>
[TestFixture]
public class PageInfoTests : JimComponentTestContext
{
    [Test]
    public void PageInfo_Renders_InfoButtonNamingThePage()
    {
        var cut = Render<PageInfo>(p => p
            .Add(c => c.Title, "Connector Space")
            .AddChildContent("What the page is for."));

        Assert.That(cut.Find("button[aria-label]").GetAttribute("aria-label"), Is.EqualTo("About Connector Space"));
    }

    [Test]
    public async Task PageInfo_Clicked_ShowsTheTitleAndDescriptionInsideThePaddedPopoverBody()
    {
        var cut = Render(WithPopoverProvider("Connector Space", "What the page is for.", null));

        await cut.Find("button[aria-label]").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            var body = cut.Find(".jim-info-popover");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(body.QuerySelector(".jim-info-popover-title")!.TextContent.Trim(), Is.EqualTo("Connector Space"));
                Assert.That(body.TextContent, Does.Contain("What the page is for."));
            }
        });
    }

    [Test]
    public async Task PageInfo_WithATerm_LinksToThatTermsGlossaryEntry()
    {
        var cut = Render(WithPopoverProvider("Connector Space", "What the page is for.", Term.ConnectorSpace));

        await cut.Find("button[aria-label]").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            var link = cut.Find(".jim-info-popover a");
            Assert.That(link.GetAttribute("href"), Is.EqualTo("https://docs.junctional.io/reference/glossary/#connector-space"));
        });
    }

    [Test]
    public async Task PageInfo_WithoutATerm_HasNoGlossaryLink()
    {
        var cut = Render(WithPopoverProvider("Operations", "What the page is for.", null));

        await cut.Find("button[aria-label]").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".jim-info-popover a"), Is.Empty));
    }

    [Test]
    public async Task TermHint_Clicked_UsesTheSamePaddedPopoverBody()
    {
        // One popover body for both affordances, so the padding and measure are fixed in one place.
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<TermHint>(1);
            builder.AddComponentParameter(2, nameof(TermHint.Term), Term.Join);
            builder.CloseComponent();
        });

        await cut.Find("button[aria-label]").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".jim-info-popover"), Has.Count.EqualTo(1)));
    }

    private static RenderFragment WithPopoverProvider(string title, string description, Term? term)
    {
        return builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<PageInfo>(1);
            builder.AddComponentParameter(2, nameof(PageInfo.Title), title);
            builder.AddComponentParameter(3, nameof(PageInfo.Term), term);
            builder.AddComponentParameter(4, nameof(PageInfo.ChildContent), (RenderFragment)(b => b.AddContent(0, description)));
            builder.CloseComponent();
        };
    }
}
