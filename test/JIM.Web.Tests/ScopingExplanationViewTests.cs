// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using Bunit;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Web.Shared;
using NUnit.Framework;
using static JIM.Web.Tests.ConnectionExplanationTestData;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="ScopingExplanationView"/> and <see cref="ScopingBulletList"/> (#348): a Synchronisation
/// Rule's scoping evaluated against one object, as the tree the rule's own criteria form, and the server's bullets.
/// Every word comes from the server, so these check that each part of the explanation is shown, in the tree's shape,
/// with each outcome marked; not how it is worded.
/// </summary>
[TestFixture]
public class ScopingExplanationViewTests : JimComponentTestContext
{
    private IRenderedComponent<ScopingExplanationView> RenderExplanation(ScopingExplanation explanation) =>
        Render<ScopingExplanationView>(p => p.Add(c => c.Explanation, explanation));

    [Test]
    public void Render_ExplanationWithCriteria_ShowsEveryCriterionWithWhatTheObjectHeld()
    {
        var cut = RenderExplanation(FinanceAppScoping());

        var criteria = cut.FindAll("[data-testid='jim-scoping-criterion']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criteria.Select(c => c.GetAttribute("data-path")), Is.EqualTo(new[] { "1.1", "1.2", "1.3.1", "1.3.2" }));
            Assert.That(criteria[1].TextContent, Does.Contain("Department equals Finance").And.Contain("is Engineering"));
            Assert.That(criteria[2].TextContent, Does.Contain("Cost Centre starts with FIN").And.Contain("has no value"));
        }
    }

    /// <summary>
    /// Outcomes are conveyed by more than colour (the PRD's accessibility requirement): each criterion says which it
    /// is, in a form assistive technology reads.
    /// </summary>
    [Test]
    public void Render_ExplanationWithCriteria_MarksEachCriterionsOutcomeAccessibly()
    {
        var cut = RenderExplanation(FinanceAppScoping());

        var criteria = cut.FindAll("[data-testid='jim-scoping-criterion']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criteria.Select(c => c.GetAttribute("data-outcome")),
                Is.EqualTo(new[] { "Met", "NotMet", "NoValue", "NotMet" }));
            Assert.That(criteria.Select(c => c.QuerySelector("[role='img']")?.GetAttribute("aria-label")),
                Has.All.Not.Null.And.All.Not.Empty);
        }
    }

    [Test]
    public void Render_NestedGroup_IsShownInsideItsParentWithItsOwnHeading()
    {
        var cut = RenderExplanation(FinanceAppScoping());

        var nested = cut.Find("[data-testid='jim-scoping-group'][data-path='1.3']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nested.ParentElement!.Closest("[data-testid='jim-scoping-group']")!.GetAttribute("data-path"), Is.EqualTo("1"));
            Assert.That(nested.TextContent, Does.Contain("Any one of these (none met)"));
            Assert.That(nested.QuerySelectorAll("[data-testid='jim-scoping-criterion']").Select(c => c.GetAttribute("data-path")),
                Is.EqualTo(new[] { "1.3.1", "1.3.2" }));
        }
    }

    [Test]
    public void Render_Explanation_StatesTheRulesOutcome()
    {
        var cut = RenderExplanation(FinanceAppScoping());

        Assert.That(cut.Find("[data-testid='jim-scoping-explanation']").GetAttribute("data-outcome"), Is.EqualTo("OutOfScope"));
    }

    [Test]
    public void Render_Explanation_LinksToTheSynchronisationRule()
    {
        var cut = RenderExplanation(FinanceAppScoping());

        Assert.That(cut.FindAll("a").Select(a => a.GetAttribute("href")), Does.Contain("/admin/sync-rules/7"));
    }

    [Test]
    public void Render_RuleWithoutCriteria_SaysSoInsteadOfAnEmptyTree()
    {
        var cut = RenderExplanation(InScopeWithoutCriteria(2, "HR Users Import", SyncRuleDirection.Import));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid='jim-scoping-no-criteria']"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll("[data-testid='jim-scoping-group']"), Is.Empty);
        }
    }

    /// <summary>
    /// A rule's top-level groups are alternatives: the object is in scope if any one is met. With one group the group
    /// is shown directly; with several they are presented as a choice, each in full.
    /// </summary>
    [Test]
    public void Render_SeveralTopLevelGroups_PresentsThemAsAlternatives()
    {
        var explanation = FinanceAppScoping();
        explanation.Groups.Add(new ScopingGroupExplanation
        {
            Path = "2",
            Type = SearchGroupType.All,
            Met = false,
            Description = "All of these must be met (not met)",
            Criteria = [Criterion("2.1", ScopingCriterionOutcome.NotMet, "Worker Type equals Contractor", "is Employee")]
        });

        var cut = RenderExplanation(explanation);

        var alternatives = cut.Find("[data-testid='jim-scoping-alternatives']");
        Assert.That(alternatives.QuerySelectorAll(":scope > [data-testid='jim-scoping-group']").Select(g => g.GetAttribute("data-path")),
            Is.EqualTo(new[] { "1", "2" }));
    }

    [Test]
    public void Render_SingleTopLevelGroup_ShowsItDirectly()
    {
        var cut = RenderExplanation(FinanceAppScoping());

        Assert.That(cut.FindAll("[data-testid='jim-scoping-alternatives']"), Is.Empty);
    }

    [Test]
    public void ScopingBulletList_Render_ShowsEachBulletsTextInOrder()
    {
        var bullets = FinanceAppBullets();

        var cut = Render<ScopingBulletList>(p => p.Add(c => c.Bullets, bullets));

        var items = cut.FindAll("[data-testid='jim-scoping-bullet']");
        Assert.That(items.Select(i => i.TextContent.Trim()), Is.EqualTo(bullets.Select(b => b.PlainText)));
    }

    /// <summary>
    /// A masked credential value arrives as a Hidden segment; the list shows the segment's own words, never anything
    /// in its place.
    /// </summary>
    [Test]
    public void ScopingBulletList_HiddenSegment_ShowsTheServersWordsForIt()
    {
        var bullet = Bullet((ExplanationSegmentKind.Attribute, "Password"), (ExplanationSegmentKind.Text, " must equal "),
            (ExplanationSegmentKind.Hidden, "a hidden value"));

        var cut = Render<ScopingBulletList>(p => p.Add(c => c.Bullets, [bullet]));

        Assert.That(cut.Find("[data-testid='jim-scoping-bullet']").TextContent.Trim(), Is.EqualTo("Password must equal a hidden value"));
    }
}
