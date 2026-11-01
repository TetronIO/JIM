// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A collapsible section of a settings form (#2471): closed, it shows what it is set to; it never stays closed over
/// something that needs attention.
/// </summary>
[TestFixture]
public class SettingsSectionTests : JimComponentTestContext
{
    private IRenderedComponent<SettingsSection> RenderSection(string? attention = null) => Render<SettingsSection>(p => p
        .Add(c => c.Title, "Missing Input Behaviour")
        .Add(c => c.Summary, "Evaluate anyway")
        .Add(c => c.Attention, attention)
        .Add(c => c.ChildContent, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span class=\"body\">options</span>"))));

    [Test]
    public void SettingsSection_Closed_ShowsTheSummaryNotTheBody()
    {
        var cut = RenderSection();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".body"), Is.Empty);
            Assert.That(cut.Markup, Does.Contain("Evaluate anyway"));
        }
    }

    [Test]
    public async Task SettingsSection_HeaderClicked_OpensItAsync()
    {
        var cut = RenderSection();

        await cut.Find("button").ClickAsync();

        Assert.That(cut.FindAll(".body"), Has.Count.EqualTo(1));
    }

    [Test]
    public void SettingsSection_AttentionAppears_OpensItself()
    {
        var cut = RenderSection();

        cut.Render(p => p.Add(c => c.Attention, "Fails the whole object"));

        Assert.That(cut.FindAll(".body"), Has.Count.EqualTo(1), "a closed section must not hide something that needs attention");
    }

    [Test]
    public async Task SettingsSection_ClosedAgainWithAttentionStanding_StaysClosedAsync()
    {
        // Opening on attention is a prompt, not a lock: the administrator can close it once seen.
        var cut = RenderSection(attention: "Fails the whole object");

        await cut.Find("button").ClickAsync();
        cut.Render(p => p.Add(c => c.Summary, "Fail the object"));

        Assert.That(cut.FindAll(".body"), Is.Empty);
    }

    [Test]
    public async Task SettingsSection_OpenByDefaultThenClosed_StaysClosedWhenTheFormRerendersAsync()
    {
        // A section the form opens by default is passed Expanded on every render of the form; closing it must stick.
        var cut = Render<SettingsSection>(p => p
            .Add(c => c.Title, "Uniqueness token")
            .Add(c => c.Expanded, true)
            .Add(c => c.ChildContent, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span class=\"body\">options</span>"))));

        await cut.Find("button").ClickAsync();
        cut.Render(p => p.Add(c => c.Expanded, true));

        Assert.That(cut.FindAll(".body"), Is.Empty);
    }
}
