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
            .Add(c => c.ChildContent, (RenderFragment)(builder => { })));

        var tabs = cut.FindComponent<MudTabs>().Instance;
        Assert.Multiple(() =>
        {
            Assert.That(tabs.Outlined, Is.True, "the bar must carry a border");
            Assert.That(tabs.Rounded, Is.True, "the bar must be rounded like every other tab bar in JIM");
            Assert.That(tabs.Elevation, Is.EqualTo(0), "JIM's tab bars are flat, not raised");
            Assert.That(tabs.ApplyEffectsToContainer, Is.False,
                "the border and rounding belong to the bar, not to the panel container beneath it");
        });
    }

    [Test]
    public void Defaults_AreOverridableByACallSiteThatWantsSomethingElse()
    {
        var cut = Render<NavigableMudTabs>(p => p
            .Add(c => c.Outlined, false)
            .Add(c => c.Rounded, false)
            .Add(c => c.ChildContent, (RenderFragment)(builder => { })));

        var tabs = cut.FindComponent<MudTabs>().Instance;
        Assert.Multiple(() =>
        {
            Assert.That(tabs.Outlined, Is.False);
            Assert.That(tabs.Rounded, Is.False);
        });
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

    private static string FindWebProjectRoot()
    {
        var directory = new DirectoryInfo(NUnit.Framework.TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "JIM.Web")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "could not locate the repository root from the test directory");
        return Path.Combine(directory!.FullName, "src", "JIM.Web");
    }
}
