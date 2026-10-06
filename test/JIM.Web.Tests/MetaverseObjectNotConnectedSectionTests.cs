// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using Bunit;
using JIM.Models.Staging.DTOs;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;
using static JIM.Web.Tests.ConnectionExplanationTestData;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="MetaverseObjectNotConnectedSection"/> (#348): the Connections tab's collapsible list of
/// enabled export Synchronisation Rules whose Connected System holds no object joined to this Metaverse Object, each
/// with its reason, a one-line hint, the bullets in its expansion only, and Copy summary.
/// </summary>
[TestFixture]
public class MetaverseObjectNotConnectedSectionTests : JimComponentTestContext
{
    private const string CopyToClipboard = "jimInterop.copyToClipboard";

    private IRenderedComponent<MetaverseObjectNotConnectedSection> RenderSection(IReadOnlyList<NotConnectedEntry> entries,
        bool open = false, ISet<int>? expandedRuleIds = null) =>
        Render<MetaverseObjectNotConnectedSection>(p => p
            .Add(c => c.Entries, entries)
            .Add(c => c.EvaluatedAt, EvaluatedAt)
            .Add(c => c.Open, open)
            .Add(c => c.ExpandedRuleIds, expandedRuleIds ?? new HashSet<int>()));

    private static IEnumerable<string?> RuleIds(IRenderedComponent<MetaverseObjectNotConnectedSection> cut) =>
        cut.FindAll("[data-testid='jim-not-connected-expand']").Select(r => r.GetAttribute("data-rule-id"));

    /// <summary>Each entry's row, found from its expand button (the table draws the row itself).</summary>
    private static List<IElement> Rows(IRenderedComponent<MetaverseObjectNotConnectedSection> cut) =>
        cut.FindAll("[data-testid='jim-not-connected-expand']").Select(b => b.Closest("tr")!).ToList();

    [Test]
    public void Render_ByDefault_IsCollapsedAndCountsItsEntries()
    {
        var cut = RenderSection([FinanceApp(), LearningPlatform()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid='jim-not-connected-count']").TextContent.Trim(), Is.EqualTo("2"));
            Assert.That(cut.Find("[data-testid='jim-not-connected-toggle']").GetAttribute("aria-expanded"), Is.EqualTo("false"));
            Assert.That(RuleIds(cut), Is.Empty);
        }
    }

    [Test]
    public void Click_Toggle_OpensTheSectionAndRaisesOpenChanged()
    {
        bool? opened = null;
        var cut = Render<MetaverseObjectNotConnectedSection>(p => p
            .Add(c => c.Entries, [FinanceApp(), LearningPlatform()])
            .Add(c => c.ExpandedRuleIds, new HashSet<int>())
            .Add(c => c.OpenChanged, (bool value) => opened = value));

        cut.Find("[data-testid='jim-not-connected-toggle']").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened, Is.True);
            Assert.That(RuleIds(cut), Is.EqualTo(new[] { "7", "8" }));
        }
    }

    [Test]
    public void Render_Open_ShowsEachEntrysSystemRuleReasonAndHint()
    {
        var cut = RenderSection([FinanceApp(), LearningPlatform()], open: true);

        var rows = Rows(cut);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows[0].TextContent, Does.Contain("Finance App").And.Contain("Finance App Users Export"));
            Assert.That(rows[0].QuerySelector("[data-testid='jim-not-connected-reason']")!.GetAttribute("data-reason"), Is.EqualTo("NotInScope"));
            Assert.That(rows[0].QuerySelector("[data-testid='jim-not-connected-hint']")!.TextContent.Trim(),
                Is.EqualTo("Fails on Department; Cost Centre or Job Title"));
            Assert.That(rows[1].QuerySelector("[data-testid='jim-not-connected-reason']")!.GetAttribute("data-reason"), Is.EqualTo("NotYetProvisioned"));
            Assert.That(rows[1].QuerySelector("[data-testid='jim-not-connected-hint']")!.TextContent.Trim(), Is.EqualTo("In scope; nothing staged yet"));
        }
    }

    /// <summary>
    /// The hint is one line that may be clipped, so its full text stays reachable on hover.
    /// </summary>
    [Test]
    public void Render_Open_KeepsTheFullHintOnItsTitle()
    {
        var cut = RenderSection([FinanceApp()], open: true);

        Assert.That(cut.Find("[data-testid='jim-not-connected-hint']").GetAttribute("title"),
            Is.EqualTo("Fails on Department; Cost Centre or Job Title"));
    }

    [Test]
    public void Render_Open_KeepsTheBulletsOutOfTheRow()
    {
        var cut = RenderSection([FinanceApp()], open: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid='jim-not-connected-detail']"), Is.Empty);
            Assert.That(cut.FindAll("[data-testid='jim-scoping-bullet']"), Is.Empty);
        }
    }

    [Test]
    public void Click_ExpandOnANotInScopeEntry_ShowsItsBulletsAndScopingTree()
    {
        var expanded = new HashSet<int>();
        var cut = RenderSection([FinanceApp()], open: true, expandedRuleIds: expanded);

        cut.Find("[data-testid='jim-not-connected-expand']").Click();

        var detail = cut.Find("[data-testid='jim-not-connected-detail']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expanded, Does.Contain(7), "what the reader opened is kept on the state the page passed in");
            Assert.That(detail.TextContent, Does.Contain("To come into scope"));
            Assert.That(detail.QuerySelectorAll("[data-testid='jim-scoping-bullet']").Select(b => b.TextContent.Trim()),
                Is.EqualTo(FinanceAppBullets().Select(b => b.PlainText)));
            Assert.That(detail.QuerySelectorAll("[data-testid='jim-scoping-explanation']"), Has.Length.EqualTo(1));
        }
    }

    /// <summary>
    /// A reason other than Not in scope or Rule misconfigured is not about failing criteria: its expansion says what
    /// happens next and does not show a tree of criteria the object already meets.
    /// </summary>
    [Test]
    public void Render_ExpandedNotYetProvisionedEntry_ShowsWhatHappensNextWithoutATree()
    {
        var cut = RenderSection([LearningPlatform()], open: true, expandedRuleIds: new HashSet<int> { 8 });

        var detail = cut.Find("[data-testid='jim-not-connected-detail']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail.TextContent, Does.Contain("What happens next"));
            Assert.That(detail.QuerySelectorAll("[data-testid='jim-scoping-bullet']"), Has.Length.EqualTo(2));
            Assert.That(detail.QuerySelectorAll("[data-testid='jim-scoping-explanation']"), Is.Empty);
        }
    }

    [Test]
    public void Render_OpenWithNoEntries_SaysSoRatherThanDisappearing()
    {
        var cut = RenderSection([], open: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid='jim-not-connected-count']").TextContent.Trim(), Is.EqualTo("0"));
            Assert.That(cut.FindAll("[data-testid='jim-not-connected-empty']"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Click_CopySummary_CopiesTheServersSummaryWithoutExpandingAndSaysSo()
    {
        var copy = JSInterop.Setup<bool>(CopyToClipboard, FinanceAppSummary).SetResult(true);
        var cut = RenderSection([FinanceApp()], open: true);

        cut.Find("[data-testid='jim-not-connected-copy']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(copy.Invocations, Has.Count.EqualTo(1));
            Assert.That(cut.FindAll("[data-testid='jim-not-connected-detail']"), Is.Empty);
            Assert.That(Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(s => s.Message), Has.Some.Contains("copied"));
        });
    }

    /// <summary>
    /// Over plain HTTP the browser refuses the clipboard, so the copy is offered by hand instead: the summary, in a
    /// dialog, ready to select.
    /// </summary>
    [Test]
    public void Click_CopySummaryWhenTheClipboardIsUnavailable_OffersTheSummaryInADialog()
    {
        JSInterop.Setup<bool>(CopyToClipboard, FinanceAppSummary).SetResult(false);
        var provider = Render<MudDialogProvider>();
        var cut = RenderSection([FinanceApp()], open: true);

        cut.Find("[data-testid='jim-not-connected-copy']").Click();

        provider.WaitForAssertion(() =>
            Assert.That(provider.Find("[data-testid='jim-copy-text-fallback']").GetAttribute("value") ??
                        provider.Find("[data-testid='jim-copy-text-fallback']").TextContent,
                Is.EqualTo(FinanceAppSummary)));
    }
}
