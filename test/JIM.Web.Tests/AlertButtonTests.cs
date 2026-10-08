// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers AlertButton, the one way to put a button in an alert: filled in the colour of the alert's severity, so the
/// action reads as part of the message it answers, or, marked Secondary, a text button that inherits the alert's colour
/// for a lesser action beside it. AlertButtonConventionTests holds every alert to it.
/// </summary>
[TestFixture]
public class AlertButtonTests : JimComponentTestContext
{
    [TestCase(Severity.Normal, Color.Default)]
    [TestCase(Severity.Info, Color.Info)]
    [TestCase(Severity.Success, Color.Success)]
    [TestCase(Severity.Warning, Color.Warning)]
    [TestCase(Severity.Error, Color.Error)]
    public void AlertButton_OfASeverity_IsFilledInThatSeveritysColour(Severity severity, Color expected)
    {
        var cut = Render<AlertButton>(p => p.Add(c => c.Severity, severity).AddChildContent("Go"));
        var button = cut.FindComponent<MudButton>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(button.Variant, Is.EqualTo(Variant.Filled));
            Assert.That(button.Color, Is.EqualTo(expected));
        }
    }

    [Test]
    public void AlertButton_Secondary_IsATextButtonInheritingTheAlertsColour()
    {
        var cut = Render<AlertButton>(p => p.Add(c => c.Severity, Severity.Warning).Add(c => c.Secondary, true).AddChildContent("Go"));
        var button = cut.FindComponent<MudButton>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(button.Variant, Is.EqualTo(Variant.Text));
            Assert.That(button.Color, Is.EqualTo(Color.Inherit));
        }
    }

    [Test]
    public void AlertButton_Clicked_RaisesOnClick()
    {
        var clicked = false;
        var cut = Render<AlertButton>(p => p
            .Add(c => c.Severity, Severity.Info)
            .Add(c => c.OnClick, () => clicked = true)
            .AddChildContent("Go"));

        cut.Find("button").Click();

        Assert.That(clicked, Is.True);
    }

    [Test]
    public void AlertButton_WithAnAttributeOfItsOwn_PassesItToTheButton()
    {
        var cut = Render<AlertButton>(p => p
            .Add(c => c.Severity, Severity.Info)
            .AddUnmatched("data-testid", "jim-example")
            .AddChildContent("Go"));

        Assert.That(cut.Find("[data-testid='jim-example']").TextContent, Does.Contain("Go"));
    }
}
