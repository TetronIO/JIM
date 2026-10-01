// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers SegmentedToggle, the one control for choosing between a few mutually exclusive settings or views (the
/// causality panel's view switch, the Inspect view's Group by).
/// </summary>
[TestFixture]
public class SegmentedToggleTests : JimComponentTestContext
{
    private static readonly IReadOnlyList<SegmentedToggleOption<string>> Options =
    [
        new("none", "None"),
        new("source", "Source"),
        new("category", "Category")
    ];

    [Test]
    public void SegmentedToggle_RendersOneButtonPerOption_MarkingOnlyTheSelectedOne()
    {
        var cut = Render<SegmentedToggle<string>>(p => p
            .Add(c => c.Options, Options)
            .Add(c => c.Value, "source")
            .Add(c => c.AriaLabel, "Group by"));

        var buttons = cut.FindAll(".jim-seg > button");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buttons.Select(b => b.TextContent.Trim()), Is.EqualTo(new[] { "None", "Source", "Category" }));
            Assert.That(buttons.Select(b => b.GetAttribute("aria-pressed")), Is.EqualTo(new[] { "false", "true", "false" }));
            Assert.That(buttons[1].ClassList, Does.Contain("on"));
            Assert.That(cut.Find(".jim-seg").GetAttribute("aria-label"), Is.EqualTo("Group by"));
        }
    }

    [Test]
    public void SegmentedToggle_ClickingAnotherOption_RaisesValueChangedWithIt()
    {
        string? raised = null;
        var cut = Render<SegmentedToggle<string>>(p => p
            .Add(c => c.Options, Options)
            .Add(c => c.Value, "none")
            .Add(c => c.AriaLabel, "Group by")
            .Add(c => c.ValueChanged, (string v) => raised = v));

        cut.FindAll(".jim-seg > button")[2].Click();

        Assert.That(raised, Is.EqualTo("category"));
    }

    [Test]
    public void SegmentedToggle_ClickingTheSelectedOption_RaisesNothing()
    {
        var raised = false;
        var cut = Render<SegmentedToggle<string>>(p => p
            .Add(c => c.Options, Options)
            .Add(c => c.Value, "none")
            .Add(c => c.AriaLabel, "Group by")
            .Add(c => c.ValueChanged, (string _) => raised = true));

        cut.FindAll(".jim-seg > button")[0].Click();

        Assert.That(raised, Is.False);
    }

    [Test]
    public void SegmentedToggle_Dense_AddsTheCompactClass()
    {
        var cut = Render<SegmentedToggle<string>>(p => p
            .Add(c => c.Options, Options)
            .Add(c => c.Value, "none")
            .Add(c => c.AriaLabel, "Scope")
            .Add(c => c.Dense, true));

        Assert.That(cut.Find(".jim-seg").ClassList, Does.Contain("jim-seg-dense"));
    }
}
