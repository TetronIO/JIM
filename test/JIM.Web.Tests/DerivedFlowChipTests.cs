// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Derived modifier chip an Attribute Flow row wears when its expression reads the Metaverse (#1750, mockup A):
/// "Derived · step N", a Text chip in a colour none of its neighbours on the row use, with a tooltip naming what it
/// reads and when it runs.
/// </summary>
[TestFixture]
public class DerivedFlowChipTests : JimComponentTestContext
{
    private static readonly DerivedFlowStepInfo Email = new(2, 3, ["Account Name"]);

    [Test]
    public void DerivedFlowChip_OrderedFlow_ShowsItsStepAsASecondaryTextChip()
    {
        var cut = Render<DerivedFlowChip>(p => p.Add(c => c.StepInfo, Email));

        var chip = cut.FindComponent<MudChip<string>>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Find("[data-testid='jim-derived-flow-chip']").TextContent.Trim(), Is.EqualTo("Derived · step 2"));
            Assert.That(chip.Instance.Variant, Is.EqualTo(Variant.Text));
            Assert.That(chip.Instance.Color, Is.EqualTo(Color.Secondary),
                "Disabled is Warning, Initial Export Only is Info, and the Type column's chips use Info, Tertiary, Warning and Primary");
        }
    }

    [Test]
    public void DerivedFlowChip_Tooltip_NamesTheInputsAndTheStep()
    {
        var cut = Render<DerivedFlowChip>(p => p.Add(c => c.StepInfo, Email));

        var tooltip = cut.FindComponent<MudTooltip>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooltip.Arrow, Is.True);
            Assert.That(tooltip.Placement, Is.EqualTo(Placement.Top));
        }

        var content = Render(tooltip.TooltipContent!);
        Assert.That(content.FindComponent<TooltipText>().Instance.Text,
            Is.EqualTo("Reads Account Name from the Metaverse. Runs in step 2 of 3, after Account Name is resolved."));
    }
}
