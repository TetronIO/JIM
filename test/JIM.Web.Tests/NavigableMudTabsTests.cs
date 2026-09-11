// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using Bunit;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// JIM's tab bar look lives on <see cref="NavigableMudTabs"/> rather than on each page that uses it.
/// MudTabs' own defaults render an unbordered, unrounded white band that reads as a broken tab bar, and a
/// page that simply omitted the presentation parameters got exactly that; the defaults here are what stop
/// the next page repeating it.
/// </summary>
[TestFixture]
public class NavigableMudTabsTests : JimComponentTestContext
{
    [Test]
    public void Defaults_AreJimsTabLookRatherThanMudBlazors()
    {
        var cut = Render<NavigableMudTabs>(p => p
            .Add(c => c.ChildContent, Panels("Overview", "Detail")));

        var tabs = cut.FindComponent<MudTabs>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tabs.Outlined, Is.True, "the bar must carry a border");
            Assert.That(tabs.Rounded, Is.True, "the bar must be rounded like every other tab bar in JIM");
            Assert.That(tabs.Elevation, Is.EqualTo(0), "JIM's tab bars are flat, not raised");
            Assert.That(tabs.ApplyEffectsToContainer, Is.False,
                "the border and rounding belong to the bar, not to the panel container beneath it");
        }
    }

    [Test]
    public void Defaults_AreOverridableByACallSiteThatWantsSomethingElse()
    {
        var cut = Render<NavigableMudTabs>(p => p
            .Add(c => c.Outlined, false)
            .Add(c => c.Rounded, false)
            .Add(c => c.ChildContent, Panels("Overview", "Detail")));

        var tabs = cut.FindComponent<MudTabs>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tabs.Outlined, Is.False);
            Assert.That(tabs.Rounded, Is.False);
        }
    }

    /// <summary>
    /// The defaults only help pages that let them apply. A call site that spells the values out is fine, but
    /// one that spells out a <b>different</b> look would be an unexplained exception to a convention every
    /// other page follows, so it has to be a deliberate, visible choice rather than a copy-paste slip.
    /// </summary>
    [Test]
    public void NoPage_OptsOutOfTheOutlinedRoundedLook()
    {
        var offenders = Directory
            .EnumerateFiles(FindWebProjectRoot(), "*.razor", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "NavigableMudTabs.razor")
            .SelectMany(f => Regex
                .Matches(File.ReadAllText(f), @"<NavigableMudTabs\b[^>]*>", RegexOptions.Singleline)
                .Select(m => (File: Path.GetFileName(f), Tag: m.Value)))
            .Where(x => x.Tag.Contains("Outlined=\"false\"") || x.Tag.Contains("Rounded=\"false\""))
            .Select(x => x.File)
            .ToList();

        Assert.That(offenders, Is.Empty,
            "these pages turn off JIM's tab look; if that is deliberate, say why in a comment above the tag");
    }

    [Test]
    public void OneTab_HidesTheBar()
    {
        var cut = Render<NavigableMudTabs>(p => p.Add(c => c.ChildContent, Panels("Overview")));

        // The marker class, not MudBlazor's DOM: the class is JIM's, read back off the parameter we set.
        Assert.That(cut.FindComponent<MudTabs>().Instance.Class, Does.Contain("jim-tabs-single"));
    }

    [Test]
    public void TwoTabs_KeepTheBar()
    {
        var cut = Render<NavigableMudTabs>(p => p.Add(c => c.ChildContent, Panels("Overview", "Pending Export")));

        Assert.That(cut.FindComponent<MudTabs>().Instance.Class ?? string.Empty,
            Does.Not.Contain("jim-tabs-single"));
    }

    [Test]
    public void OneTab_KeepsTheCallersOwnClasses()
    {
        var cut = Render<NavigableMudTabs>(p => p
            .Add(c => c.Class, "mt-2")
            .Add(c => c.ChildContent, Panels("Overview")));

        var cls = cut.FindComponent<MudTabs>().Instance.Class;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cls, Does.Contain("mt-2"));
            Assert.That(cls, Does.Contain("jim-tabs-single"));
        }
    }

    [Test]
    public void OneTab_WithHidingTurnedOff_KeepsTheBar()
    {
        var cut = Render<NavigableMudTabs>(p => p
            .Add(c => c.HideBarWhenSingleTab, false)
            .Add(c => c.ChildContent, Panels("Overview")));

        Assert.That(cut.FindComponent<MudTabs>().Instance.Class ?? string.Empty,
            Does.Not.Contain("jim-tabs-single"));
    }

    [Test]
    public void SingleTabMarker_IsActuallyStyled()
    {
        // A class named in markup but absent from site.css compiles, renders, and silently does nothing;
        // there is no general sweep for that, so the one class this component invents checks itself.
        var css = File.ReadAllText(Path.Join(FindWebProjectRoot(), "wwwroot", "css", "site.css"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(css, Does.Contain(".jim-tabs-single .mud-tabs-tabbar"), "the bar is not hidden");
            Assert.That(css, Does.Contain(".jim-tabs-single .mud-tabs-panels"),
                "the panel padding that cleared the bar is not reclaimed");
        }
    }

    /// <summary>
    /// A page that loads a tab's data on first activation needs to know which tab that was, and the index is the
    /// wrong key: a tab gated on a role sits at a different index for different readers, so a lazy load keyed on
    /// "index 3" would silently load the wrong tab's data the day a panel is added above it. The slug is the same
    /// thing the URL carries and is stable across readers.
    /// </summary>
    [Test]
    public void ActivePanelSlugChanged_WhenATabIsClicked_ReportsThatTabsSlug()
    {
        var slugs = new List<string?>();
        var cut = Render<NavigableMudTabs>(p => p
            .Add(c => c.ActivePanelSlugChanged, slug => slugs.Add(slug))
            .Add(c => c.ChildContent, Panels("Overview", "Password History")));

        cut.FindAll(".mud-tab")[1].Click();

        cut.WaitForAssertion(() => Assert.That(slugs, Is.EqualTo(new[] { "password-history" })));
    }

    private static RenderFragment Panels(params string[] titles)
    {
        // Literal sequence numbers with a per-panel region: the analyser requires literals, and a region
        // keyed on the panel's title keeps the diffing correct across a changing panel set.
        return builder =>
        {
            foreach (var title in titles)
            {
                builder.OpenRegion(0);
                builder.OpenComponent<MudTabPanel>(0);
                builder.AddAttribute(1, nameof(MudTabPanel.Text), title);
                builder.AddAttribute(2, nameof(MudTabPanel.ChildContent),
                    (RenderFragment)(panelBuilder => panelBuilder.AddContent(0, title)));
                builder.CloseComponent();
                builder.CloseRegion();
            }
        };
    }

    private static string FindWebProjectRoot()
    {
        var directory = new DirectoryInfo(NUnit.Framework.TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Join(directory.FullName, "src", "JIM.Web")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "could not locate the repository root from the test directory");
        return Path.Join(directory!.FullName, "src", "JIM.Web");
    }
}
