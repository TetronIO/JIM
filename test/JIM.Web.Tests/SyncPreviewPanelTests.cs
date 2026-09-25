// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// bUnit tests for <see cref="SyncPreviewPanel"/> (#1519): the reusable wrapper that renders a Sync
/// Preview result through <see cref="CausalityPanel"/>, plus the preview's own blocking errors and
/// advisory warnings, which the causality tree has no node for.
/// </summary>
[TestFixture]
public class SyncPreviewPanelTests
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

    private static CausalityPageContext Context() => new(
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

    [Test]
    public void Render_SpeculativeModel_ShowsPreviewBandAndConditionalLabel()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree = [new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected }]
        };

        var cut = _context.Render<SyncPreviewPanel>(ps => ps
            .Add(c => c.PreviewResult, preview)
            .Add(c => c.Context, Context()));

        Assert.That(cut.Find(".preview-pill").TextContent.Trim(), Is.EqualTo("Preview"));
        Assert.That(cut.Markup, Does.Contain("A Metaverse Object would be projected"));
    }

    [Test]
    public void Render_PreviewWithBlockingErrors_ShowsErrorAlert()
    {
        var preview = new SyncPreviewResult
        {
            Errors = [new SyncPreviewMessage { Code = SyncPreviewMessageCode.ObjectNotFound, Detail = "The object could not be found." }]
        };

        var cut = _context.Render<SyncPreviewPanel>(ps => ps
            .Add(c => c.PreviewResult, preview)
            .Add(c => c.Context, Context()));

        var alert = cut.Find("[data-testid='jim-sync-preview-errors']");
        Assert.That(alert.TextContent, Does.Contain("The object could not be found."));
    }

    [Test]
    public void Render_PreviewWithWarningsOnly_ShowsWarningAlertNotErrorAlert()
    {
        var preview = new SyncPreviewResult
        {
            Warnings = [new SyncPreviewMessage { Code = SyncPreviewMessageCode.DownstreamDisconnectOnly, Detail = "Would disconnect without deprovisioning." }]
        };

        var cut = _context.Render<SyncPreviewPanel>(ps => ps
            .Add(c => c.PreviewResult, preview)
            .Add(c => c.Context, Context()));

        Assert.That(cut.FindAll("[data-testid='jim-sync-preview-errors']"), Is.Empty);
        var alert = cut.Find("[data-testid='jim-sync-preview-warnings']");
        Assert.That(alert.TextContent, Does.Contain("Would disconnect without deprovisioning."));
    }

    [Test]
    public void Render_PreviewWithNoMessages_ShowsNoAlerts()
    {
        var cut = _context.Render<SyncPreviewPanel>(ps => ps
            .Add(c => c.PreviewResult, new SyncPreviewResult())
            .Add(c => c.Context, Context()));

        Assert.That(cut.FindAll("[data-testid='jim-sync-preview-errors']"), Is.Empty);
        Assert.That(cut.FindAll("[data-testid='jim-sync-preview-warnings']"), Is.Empty);
        Assert.That(cut.FindAll("[data-testid='jim-sync-preview-generated-value-notice']"), Is.Empty);
    }

    /// <summary>
    /// Unique Value Generation (#242): a preview naming a generated value carries the honesty note that the
    /// value shown is only the next free one now.
    /// </summary>
    [Test]
    public void Render_PreviewWithGeneratedValue_ShowsGeneratedValueNotice()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                    Children =
                    [
                        new SyncOutcomeNode
                        {
                            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned,
                            DetailMessage = "Account Name: jallen42"
                        }
                    ]
                }
            ]
        };

        var cut = _context.Render<SyncPreviewPanel>(ps => ps
            .Add(c => c.PreviewResult, preview)
            .Add(c => c.Context, Context()));

        var notice = cut.Find("[data-testid='jim-sync-preview-generated-value-notice']");
        Assert.That(notice.TextContent, Does.Contain("Generated values shown are the next free value now."));
    }

    [Test]
    public void Render_PreviewWithoutGeneratedValue_ShowsNoGeneratedValueNotice()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree = [new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected }]
        };

        var cut = _context.Render<SyncPreviewPanel>(ps => ps
            .Add(c => c.PreviewResult, preview)
            .Add(c => c.Context, Context()));

        Assert.That(cut.FindAll("[data-testid='jim-sync-preview-generated-value-notice']"), Is.Empty);
    }
}
