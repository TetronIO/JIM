// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using JIM.Web.Causality;
using JIM.Web.Shared;
using JIM.Web.Shared.Causality;
using NUnit.Framework;
using static JIM.Web.Tests.CausalityConsequenceTestData;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for the Lineage's After this run sections (#1811): the rule that separates what this run did from what
/// followed it, the cards each kind of consequence renders as, a cohort's members paged and found on request, and the
/// footer that says whether anything is still to happen and offers to check again only while something is.
/// </summary>
[TestFixture]
public class CausalityLineageConsequenceViewTests : JimComponentTestContext
{
    [Test]
    public void Render_ObjectsTheCascadeReached_RuleOffWhatFollowedAsAfterThisRun()
    {
        var cut = RenderLineage(BuildImportLineage(CompleteCascade()));

        var objects = cut.FindAll(".ln-object");
        var record = objects[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".ln-after"), Has.Count.EqualTo(3), "the Metaverse Object, the account and the Groups");
            Assert.That(cut.FindAll(".ln-after").Select(r => r.TextContent.Trim()), Is.All.EqualTo("After this run"));
            Assert.That(record.QuerySelector(".ln-after"), Is.Null, "nothing later happened to the record itself");
        }
    }

    [Test]
    public void Render_Confirmation_IsFoldedUntilShown()
    {
        var cut = RenderLineage(BuildImportLineage(CompleteCascade()));

        var fold = cut.FindAll(".ln-fold")[0];
        Assert.That(fold.TextContent, Does.Contain("Confirmed by the next import"));
        Assert.That(fold.QuerySelector(".evt-card"), Is.Null);

        fold.QuerySelector(".ln-fold-toggle")!.Click();

        var card = cut.FindAll(".ln-fold")[0].QuerySelector(".evt-card");
        Assert.That(card, Is.Not.Null);
        Assert.That(card!.QuerySelector(".evt-title")!.TextContent.Trim(), Is.EqualTo("Export confirmed"),
            "the fold line already says it was confirmed; the card says what the import recorded");
    }

    [Test]
    public void Render_ExportStillQueued_NamesWhatTheBranchWaitsOnAndLinksIt()
    {
        var synchronisation = SynchronisationCohort();
        var pendingExportId = Guid.NewGuid();
        synchronisation.Consequences.Add(OutstandingCohort(PendingExportStatus.Pending, pendingExportId));

        var cut = RenderLineage(BuildImportLineage(Consequences(synchronisation)));

        var waiting = cut.Find(".ln-wait");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting.TextContent, Does.Contain("Waiting for the next Export of Glitterband EMEA"));
            Assert.That(waiting.QuerySelector("a")!.GetAttribute("href"),
                Is.EqualTo($"/admin/connected-systems/{Glitterband}/pending-exports/{pendingExportId}"));
        }
    }

    [Test]
    public void Render_OutcomeCard_NamesTheRunAndLinksTheItemThatRecordedIt()
    {
        var cut = RenderLineage(BuildImportLineage(CompleteCascade()));

        var deleted = cut.FindAll(".ln-later").First(c => c.TextContent.Contains("Metaverse Object deleted"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.QuerySelector(".ln-runchip")!.TextContent.Trim(), Is.EqualTo("Full Synchronisation"));
            Assert.That(deleted.QuerySelectorAll("a.ln-link").Select(a => a.TextContent.Trim()), Does.Contain("View activity item"));
        }
    }

    [Test]
    public void Render_DeprovisionCard_NamesTheDeprovisioningActionThatDecidedIt()
    {
        var cut = RenderLineage(BuildImportLineage(CompleteCascade()));

        var deprovision = cut.FindAll(".ln-later").First(c => c.TextContent.Contains("Deprovision queued"));
        var setting = deprovision.QuerySelector(".ln-setting")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.QuerySelector(".ln-setting-label")!.TextContent.Trim(), Is.EqualTo("Deprovisioning Action"));
            Assert.That(setting.QuerySelector(".ln-setting-value")!.TextContent.Trim(), Is.EqualTo("Delete"));
        }
    }

    [Test]
    public async Task Render_LargeCohort_PagesMoreMembersOnRequestAsync()
    {
        var removals = LargeRemovalCohort(memberCount: 120);
        var requests = new List<(string Key, int Page, string? Search)>();
        var cut = RenderLineage(BuildImportLineage(Consequences(removals)), loadMembers: (key, page, search) =>
        {
            requests.Add((key, page, search));
            return Task.FromResult<PagedResultSet<CausalConsequenceMember>?>(Page(page, 120, $"Group {page}"));
        });

        cut.Find(".ln-members-toggle").Click();
        Assert.That(cut.FindAll(".ln-member"), Has.Count.EqualTo(50), "the walk's first page, with no request");

        await cut.Find(".ln-members-more").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Is.EqualTo(new[] { (removals.CohortKey, 2, (string?)null) }));
            Assert.That(cut.FindAll(".ln-member"), Has.Count.EqualTo(100));
            Assert.That(cut.Find(".ln-members-count").TextContent, Does.Contain("100 of 120"));
        }
    }

    [Test]
    public async Task Render_LargeCohort_FindsMembersByNameAsync()
    {
        var removals = LargeRemovalCohort(memberCount: 120);
        var requests = new List<(string Key, int Page, string? Search)>();
        var cut = RenderLineage(BuildImportLineage(Consequences(removals)), loadMembers: (key, page, search) =>
        {
            requests.Add((key, page, search));
            return Task.FromResult<PagedResultSet<CausalConsequenceMember>?>(Page(page, 1, "Project-Pulse"));
        });
        cut.Find(".ln-members-toggle").Click();

        var search = cut.FindComponent<SearchField>();
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("pulse"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Is.EqualTo(new[] { (removals.CohortKey, 1, (string?)"pulse") }));
            Assert.That(cut.FindAll(".ln-member").Select(m => m.TextContent), Has.One.Contains("Project-Pulse"));
            Assert.That(cut.FindAll(".ln-members-more"), Is.Empty, "the one match is all there is");
        }
    }

    [Test]
    public void Render_Footer_OffersCheckAgainWhileABranchWaits()
    {
        var checkedAgain = false;
        var lineage = BuildImportLineage(Consequences(endings: [Ending(CausalConsequenceEnding.WaitingForSynchronisation, Yellowstone, "Yellowstone APAC")]));
        var cut = RenderLineage(lineage, onCheckAgain: () => checkedAgain = true);

        var footer = cut.Find(".ln-footer");
        Assert.That(footer.TextContent, Does.Contain("1 branch is waiting on a Synchronisation of Yellowstone APAC."));
        Assert.That(footer.TextContent, Does.Contain("Checked at"));

        cut.Find(".ln-check-again").Click();

        Assert.That(checkedAgain, Is.True);
    }

    [Test]
    public void Render_Footer_NeverHintsAtWhatCannotComeOnceEveryBranchHasEnded()
    {
        var cut = RenderLineage(BuildImportLineage(CompleteCascade()), onCheckAgain: () => { });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".ln-footer").TextContent, Does.Contain("Every branch has run to its end."));
            Assert.That(cut.FindAll(".ln-check-again"), Is.Empty);
        }
    }

    [Test]
    public void Render_ItemThatCausesNothing_HasNoFooterAndNoRule()
    {
        var cut = RenderLineage(BuildImportLineage(Consequences(endings: [Ending(CausalConsequenceEnding.NoFurtherConsequences)])));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".ln-footer"), Is.Empty);
            Assert.That(cut.FindAll(".ln-after"), Is.Empty);
        }
    }

    [Test]
    public void Render_WhileTheWalkIsRead_SaysSoInsteadOfAFooter()
    {
        var cut = RenderLineage(BuildImportLineage(consequences: null), loading: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".ln-consequences-loading").TextContent, Does.Contain("what this run went on to cause"));
            Assert.That(cut.FindAll(".ln-footer"), Is.Empty);
        }
    }

    private IRenderedComponent<CausalityLineageView> RenderLineage(CausalityLineageModel model,
        Func<string, int, string?, Task<PagedResultSet<CausalConsequenceMember>?>>? loadMembers = null,
        Action? onCheckAgain = null, bool loading = false)
    {
        return Render<CausalityLineageView>(ps =>
        {
            ps.Add(c => c.Model, model);
            ps.Add(c => c.ConsequencesLoading, loading);
            if (loadMembers != null)
                ps.Add(c => c.LoadConsequenceMembers, loadMembers);
            if (onCheckAgain != null)
                ps.Add(c => c.OnCheckAgain, onCheckAgain);
        });
    }

    private static CausalConsequenceCohort LargeRemovalCohort(int memberCount)
    {
        var cascade = CompleteCascade();
        var removals = cascade.Cohorts[0].Consequences.Single(c => c.EdgeType == CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval);
        var large = new CausalConsequenceCohort
        {
            CohortKey = removals.CohortKey,
            HopKind = removals.HopKind,
            EdgeType = removals.EdgeType,
            ReasonCode = removals.ReasonCode,
            ConnectedSystemId = removals.ConnectedSystemId,
            ConnectedSystemName = removals.ConnectedSystemName,
            ObjectTypeName = removals.ObjectTypeName,
            ObjectTypePluralName = removals.ObjectTypePluralName,
            AttributeName = removals.AttributeName,
            MemberCount = memberCount,
            FirstOccurred = removals.FirstOccurred,
            LastOccurred = removals.LastOccurred,
            Members = Enumerable.Range(0, 50)
                .Select(i => new CausalConsequenceMember { RunProfileExecutionItemId = Guid.NewGuid(), DisplayName = $"Group {i:000}", Occurred = T0 })
                .ToList(),
            Representative = removals.Representative
        };
        return large;
    }

    private static PagedResultSet<CausalConsequenceMember> Page(int page, int total, string namePrefix) => new()
    {
        Results = Enumerable.Range(0, Math.Min(50, total))
            .Select(i => new CausalConsequenceMember { RunProfileExecutionItemId = Guid.NewGuid(), DisplayName = total == 1 ? namePrefix : $"{namePrefix}.{i}", Occurred = T0 })
            .ToList(),
        TotalResults = total,
        PageSize = 50,
        CurrentPage = page
    };
}
