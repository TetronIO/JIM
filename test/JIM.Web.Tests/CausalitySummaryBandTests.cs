// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalitySummaryBand"/>: sentence segment rendering, entity token
/// links, encoding of hostile connected-system values, and tone-classed outcome pills.
/// </summary>
[TestFixture]
public class CausalitySummaryBandTests
{
    private static IRenderedComponent<CausalitySummaryBand> RenderBand(
        BunitContext context,
        ActivityRunProfileExecutionItem item,
        CausalityPageContext pageContext,
        DateTime? timestamp = null)
    {
        var model = CausalityModelBuilder.Build(item, pageContext);
        var summary = CausalitySummaryBuilder.Build(model);
        return context.Render<CausalitySummaryBand>(ps => ps
            .Add(c => c.Summary, summary)
            .Add(c => c.Context, pageContext)
            .Add(c => c.Timestamp, timestamp));
    }

    [Test]
    public async Task Render_NewJoinerScenario_RendersTheFullSentenceTextAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderBand(context, CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var sentence = cut.Find(".summary-sentence").TextContent;
        Assert.That(sentence, Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC processed the record for Liam Allen (S8-287551): " +
            "a new Identity was created, 11 attributes flowed to it, and an export of 11 changes is now queued for Glitterband EMEA."));
    }

    [Test]
    public async Task Render_NewJoinerScenario_RendersEntityTokensAsLinksWithCorrectHrefsAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderBand(context, CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var linkedTokens = cut.FindAll("a.hl");
        Assert.That(linkedTokens.Select(a => (a.TextContent, a.GetAttribute("href"))), Is.EqualTo(new[]
        {
            ("Yellowstone APAC", "/admin/connected-systems/1"),
            ("Liam Allen (S8-287551)", $"/admin/connected-systems/1/connector-space/{CausalityTestData.CsoId}"),
            ("Glitterband EMEA", "/admin/connected-systems/2")
        }));

        // The Run Profile has no detail page, so its token renders unlinked
        var unlinkedTokens = cut.FindAll("span.hl");
        Assert.That(unlinkedTokens.Select(s => s.TextContent), Does.Contain("Full Synchronisation"));
    }

    [Test]
    public async Task Render_HostileDisplayName_IsEncodedNotExecutedAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var hostileContext = new CausalityPageContext(
            ConnectedSystemId: 1,
            ConnectedSystemName: "Yellowstone APAC",
            RunProfileName: "Full Synchronisation",
            CsoId: CausalityTestData.CsoId,
            CsoDisplayName: "<script>alert('xss')</script>",
            CsoExternalId: "S8-1",
            CsoObjectTypeName: "person",
            MvoTypeName: "Person",
            MvoTypePluralName: "People");

        var cut = RenderBand(context, CausalityTestData.NewJoinerItem(), hostileContext);

        // The hostile value must survive as text content but never become a live element
        Assert.That(cut.FindAll("script"), Is.Empty);
        Assert.That(cut.Find(".summary-sentence").TextContent, Does.Contain("<script>alert('xss')</script>"));
        Assert.That(cut.Markup, Does.Contain("&lt;script&gt;"));
    }

    [Test]
    public async Task Render_NewJoinerScenario_RendersPillsWithToneClassesAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderBand(context, CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var pills = cut.FindAll(".oc-pill");
        Assert.That(pills, Has.Count.EqualTo(4));
        Assert.That(pills[0].ClassList, Does.Contain("primary"));
        Assert.That(pills[0].TextContent.Trim(), Is.EqualTo("Identity created"));
        Assert.That(pills[1].ClassList, Does.Contain("secondary"));
        Assert.That(pills[3].ClassList, Does.Contain("info"));
        Assert.That(pills[3].TextContent.Trim(), Is.EqualTo("Export queued · 11 changes"));
    }

    [Test]
    public async Task Render_WithRunContext_RendersTheRunChipAndTimestampAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var executed = new DateTime(2026, 7, 22, 14, 32, 7, DateTimeKind.Utc);

        var cut = RenderBand(context, CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext(), executed);

        var runChip = cut.Find(".run-chip");
        Assert.That(runChip.TextContent, Does.Contain("Yellowstone APAC"));
        Assert.That(runChip.TextContent, Does.Contain("Full Synchronisation"));
        Assert.That(cut.FindAll(".summary-time"), Has.Count.EqualTo(1));
    }
}
