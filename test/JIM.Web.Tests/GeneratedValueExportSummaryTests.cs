// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The export Activity's band for Collision Remediation (Unique Value Generation, #242, release 4, Phase 9), built from the
/// run's two statistics: shown only when the run met a rejected generated value, with a pill for each count that is not
/// zero.
/// </summary>
[TestFixture]
public class GeneratedValueExportSummaryTests : JimComponentTestContext
{
    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private IRenderedComponent<GeneratedValueExportSummary> RenderSummary(int corrected, int needingDecision) =>
        Render<GeneratedValueExportSummary>(p => p
            .Add(c => c.Corrected, corrected)
            .Add(c => c.NeedingDecision, needingDecision)
            .Add(c => c.ConnectedSystemId, 2)
            .Add(c => c.ConnectedSystemName, "Corporate AD"));

    [Test]
    public void Render_NoRejectedGeneratedValues_RendersNothing() =>
        Assert.That(RenderSummary(0, 0).Markup.Trim(), Is.Empty);

    [Test]
    public void Render_CorrectedAndHeld_ShowsBothPills()
    {
        var cut = RenderSummary(3, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid='generated-value-export-corrected']").TextContent, Does.Contain("3"));
            Assert.That(cut.Find("[data-testid='generated-value-export-needs-decision']").TextContent, Does.Contain("1"));
        }
    }

    [Test]
    public void Render_OnlyHeld_ShowsOnlyTheDecisionPill()
    {
        var cut = RenderSummary(0, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid='generated-value-export-corrected']"), Is.Empty);
            Assert.That(cut.FindAll("[data-testid='generated-value-export-needs-decision']"), Has.Count.EqualTo(1));
        }
    }
}
