// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Models;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers SegmentedControl, the one slider for choosing between a few mutually exclusive options (the causality
/// panel's view switch and the Inspect view's Group by). It was the causality panel's own <c>.seg</c> until #399
/// needed it a second time.
/// </summary>
[TestFixture]
public class SegmentedControlTests : JimComponentTestContext
{
    private static readonly IReadOnlyList<SegmentedOption<string>> Options =
    [
        new("none", "None"),
        new("source", "Source"),
        new("category", "Category")
    ];

    [Test]
    public void SegmentedControl_RendersOneButtonPerOption_MarkingOnlyTheSelectedOne()
    {
        var cut = Render<SegmentedControl<string>>(p => p
            .Add(c => c.Options, Options)
            .Add(c => c.Value, "source")
            .Add(c => c.AriaLabel, "Group by"));

        var buttons = cut.FindAll(".jim-segmented button");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buttons.Select(b => b.TextContent.Trim()), Is.EqualTo(new[] { "None", "Source", "Category" }));
            Assert.That(buttons.Select(b => b.GetAttribute("aria-pressed")), Is.EqualTo(new[] { "false", "true", "false" }));
            Assert.That(buttons[1].ClassList, Does.Contain("on"));
            Assert.That(cut.Find(".jim-segmented").GetAttribute("aria-label"), Is.EqualTo("Group by"));
        }
    }

    [Test]
    public void SegmentedControl_ClickingAnotherOption_RaisesValueChangedWithIt()
    {
        string? raised = null;
        var cut = Render<SegmentedControl<string>>(p => p
            .Add(c => c.Options, Options)
            .Add(c => c.Value, "none")
            .Add(c => c.AriaLabel, "Group by")
            .Add(c => c.ValueChanged, (string v) => raised = v));

        cut.FindAll(".jim-segmented button")[2].Click();

        Assert.That(raised, Is.EqualTo("category"));
    }

    [Test]
    public void SegmentedControl_ClickingTheSelectedOption_RaisesNothing()
    {
        var raised = false;
        var cut = Render<SegmentedControl<string>>(p => p
            .Add(c => c.Options, Options)
            .Add(c => c.Value, "none")
            .Add(c => c.AriaLabel, "Group by")
            .Add(c => c.ValueChanged, (string _) => raised = true));

        cut.FindAll(".jim-segmented button")[0].Click();

        Assert.That(raised, Is.False);
    }
}
