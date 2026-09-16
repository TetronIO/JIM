// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="TermHint"/>: the info affordance that explains a JIM term where an
/// administrator first meets it (#1670).
/// </summary>
[TestFixture]
public class TermHintTests : JimComponentTestContext
{
    [Test]
    public void TermHint_Renders_InfoIconWithAriaLabelNamingTheTerm()
    {
        var cut = Render<TermHint>(p => p.Add(c => c.Term, Term.Projection));

        var button = cut.Find("button[aria-label]");

        Assert.That(button.GetAttribute("aria-label"), Is.EqualTo("About Projection"));
    }

    [Test]
    public async Task TermHint_Clicked_OpensPopoverWithTermAndDefinition()
    {
        // MudMenu renders its content through MudPopoverProvider, which (unlike most components bUnit can
        // add to its root render tree) has no ChildContent/Body of its own, so it has to be rendered
        // alongside TermHint as a sibling rather than via RenderTree.Add.
        var cut = Render(RenderWithPopoverProvider(Term.Join));

        await cut.Find("button[aria-label]").ClickAsync(new MouseEventArgs());

        var expected = TermDefinitions.For(Term.Join);
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain(expected.DisplayName));
            Assert.That(cut.Markup, Does.Contain(expected.Text));
        });
    }

    [Test]
    public async Task TermHint_Clicked_GlossaryLinkPointsAtTheTermsAnchor()
    {
        var cut = Render(RenderWithPopoverProvider(Term.PendingExport));

        await cut.Find("button[aria-label]").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            var link = cut.Find("a");
            Assert.That(link.GetAttribute("href"), Is.EqualTo("https://docs.junctional.io/reference/glossary/#pending-export"));
            Assert.That(link.GetAttribute("target"), Is.EqualTo("_blank"));
        });
    }

    private static RenderFragment RenderWithPopoverProvider(Term term)
    {
        return builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<TermHint>(1);
            builder.AddComponentParameter(2, nameof(TermHint.Term), term);
            builder.CloseComponent();
        };
    }
}
