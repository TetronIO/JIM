// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the detected facts strip under the form on a Connected System's Details tab: the compact rendering
/// that replaced the Directory Capabilities card. Three states, each meaning something different to an
/// administrator: the Connector cannot detect facts (nothing at all is shown), it can but has not connected yet
/// (one line saying so), and it has (one label-over-value item per fact).
/// </summary>
[TestFixture]
public class ConnectedSystemDetectedFactsTests : JimComponentTestContext
{
    private const string StripMarker = "jim-detected-facts";
    private const string FactMarker = "jim-detected-fact";
    private const string EmptyMarker = "jim-detected-facts-empty";

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private IRenderedComponent<ConnectedSystemDetectedFacts> RenderStrip(List<ConnectorCapability>? capabilities) =>
        Render<ConnectedSystemDetectedFacts>(p => p.Add(c => c.Capabilities, capabilities));

    /// <summary>
    /// A Connector that cannot detect facts about its target has nothing to say, and an empty strip or a hint
    /// would read as something missing rather than as something that does not apply.
    /// </summary>
    [Test]
    public void DetectedFacts_WhenTheConnectorCannotDetectFacts_RendersNothing()
    {
        var cut = RenderStrip(null);

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    /// <summary>
    /// Before the first successful connection there is nothing detected yet. One muted line says so, in place
    /// of the card holding an alert that used to.
    /// </summary>
    [Test]
    public void DetectedFacts_BeforeTheFirstConnection_SaysNothingIsDetectedYet()
    {
        var cut = RenderStrip([]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find($"[data-testid='{EmptyMarker}']").TextContent, Does.Contain("Nothing detected yet"));
            Assert.That(cut.FindAll($"[data-testid='{FactMarker}']"), Is.Empty);
        }
    }

    /// <summary>
    /// Each detected fact is a label over a value, in the order the Connector reported them, so Directory Type
    /// reads first where the Connector puts it first.
    /// </summary>
    [Test]
    public void DetectedFacts_WhenFactsWereDetected_RendersOneLabelledItemPerFact()
    {
        var cut = RenderStrip(
        [
            new ConnectorCapability { Name = "Directory Type", Value = "389 Directory Server" },
            new ConnectorCapability { Name = "Vendor", Value = "389 Project" },
            new ConnectorCapability { Name = "Paging", Value = "Supported" }
        ]);

        var facts = cut.FindAll($"[data-testid='{FactMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll($"[data-testid='{StripMarker}']"), Has.Count.EqualTo(1));
            Assert.That(facts, Has.Count.EqualTo(3));
            Assert.That(facts[0].QuerySelector(".jim-fact-label")?.TextContent, Is.EqualTo("Directory Type"));
            Assert.That(facts[0].QuerySelector(".jim-fact-value")?.TextContent, Is.EqualTo("389 Directory Server"));
            Assert.That(facts[2].QuerySelector(".jim-fact-value")?.TextContent, Is.EqualTo("Supported"));
            Assert.That(cut.FindAll($"[data-testid='{EmptyMarker}']"), Is.Empty);
        }
    }

    /// <summary>
    /// The explanation that used to be a paragraph on the card lives in the info button now, so the strip must
    /// still say, somewhere a reader can open, that reading it never opens a connection.
    /// </summary>
    [Test]
    public void DetectedFacts_CarriesTheExplanationInAnInfoButton_NotAParagraph()
    {
        var cut = RenderStrip([new ConnectorCapability { Name = "Paging", Value = "Supported" }]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[aria-label='About Detected']"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Not.Contain("shown here for reference only"));
        }
    }
}
