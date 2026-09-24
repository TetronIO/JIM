// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityPanel"/>'s speculative (Sync Preview) rendering path (#1519):
/// the "Preview" band, the dashed frame, the Timeline-only view switcher and the close callback.
/// </summary>
[TestFixture]
public class CausalityPanelSpeculativeTests
{
    private BunitContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _context = CausalityBunitContext.Create();
        _context.Services.AddSingleton<JIM.Web.Services.IUserPreferenceService>(new FakeUserPreferenceService());
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _context.DisposeAsync();
    }

    private static CausalityPageContext PreviewContext() => new(
        ConnectedSystemId: null,
        ConnectedSystemName: null,
        RunProfileName: "Full Synchronisation",
        CsoId: null,
        CsoConnectedSystemId: 2,
        CsoConnectedSystemName: "Glitterband EMEA",
        CsoDisplayName: "Liam Allen",
        CsoExternalId: "S8-287551",
        CsoObjectTypeName: "person",
        MvoTypeName: "Person",
        MvoTypePluralName: "People");

    private static SyncPreviewResult SimplePreview() => new()
    {
        OutcomeTree = [new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected }]
    };

    [Test]
    public void Render_PreviewResult_ShowsPreviewBandNamingTheTargetSystem()
    {
        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.PreviewResult, SimplePreview())
            .Add(c => c.Context, PreviewContext()));

        Assert.That(cut.Find(".preview-pill").TextContent.Trim(), Is.EqualTo("Preview"));
        Assert.That(cut.Find(".preview-text").TextContent, Does.Contain("Glitterband EMEA"));
        Assert.That(cut.Find(".causality-panel").ClassList, Does.Contain("speculative"));
    }

    [Test]
    public void Render_PreviewResult_RunChipReadsWouldRun()
    {
        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.PreviewResult, SimplePreview())
            .Add(c => c.Context, PreviewContext()));

        Assert.That(cut.Find(".run-chip").TextContent, Does.Contain("Would run: Full Synchronisation"));
    }

    [Test]
    public void Render_PreviewResult_OffersTimelineAndTableWithNoLineage()
    {
        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.PreviewResult, SimplePreview())
            .Add(c => c.Context, PreviewContext()));

        var buttons = cut.FindAll(".jim-segmented button");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buttons.Select(b => b.TextContent.Trim()), Is.EqualTo(new[] { "Timeline", "Table" }));
            Assert.That(buttons[0].ClassList, Does.Contain("on"), "Timeline is the speculative default");
            Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(".ln-canvas"), Is.Empty);
        }
    }

    [Test]
    public void Render_PreviewResult_SelectingTableRendersTheTableView()
    {
        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.PreviewResult, SimplePreview())
            .Add(c => c.Context, PreviewContext()));

        cut.FindAll(".jim-segmented button").Single(b => b.TextContent.Trim() == "Table").Click();

        Assert.That(cut.FindAll(".tv"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".tl"), Is.Empty);
    }

    [Test]
    public void Render_PreviewResult_SpeculativeLabelAppearsOnTheCard()
    {
        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.PreviewResult, SimplePreview())
            .Add(c => c.Context, PreviewContext()));

        Assert.That(cut.Markup, Does.Contain("A Metaverse Object would be projected"));
    }

    [Test]
    public void Render_PreviewResultWithOnClose_ClickingCloseRaisesCallback()
    {
        var closed = false;
        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.PreviewResult, SimplePreview())
            .Add(c => c.Context, PreviewContext())
            .Add(c => c.OnClose, () => closed = true));

        cut.Find(".preview-close").Click();

        Assert.That(closed, Is.True);
    }

    [Test]
    public void Render_PreviewResultWithNoOnClose_RendersNoCloseButton()
    {
        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.PreviewResult, SimplePreview())
            .Add(c => c.Context, PreviewContext()));

        Assert.That(cut.FindAll(".preview-close"), Is.Empty);
    }
}
