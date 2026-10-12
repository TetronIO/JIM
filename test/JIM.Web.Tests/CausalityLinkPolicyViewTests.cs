// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using Bunit;
using JIM.Models.Enums;
using JIM.Web.Causality;
using JIM.Web.Shared;
using JIM.Web.Shared.Causality;
using NUnit.Framework;
using static JIM.Web.Tests.CausalityConsequenceTestData;

namespace JIM.Web.Tests;

/// <summary>
/// The Lineage's cards write item links of their own (a chain hop's, a later run's, a cohort's members'), so the policy
/// the Lineage was built with must reach them too, both its addresses and what an item link says (#2100).
/// </summary>
[TestFixture]
public class CausalityLinkPolicyViewTests : JimComponentTestContext
{
    [Test]
    public void Render_ChainHop_LinksItsItemAndSaysSoInThePolicysWords()
    {
        var (model, chain) = CausalityLinkPolicyTests.ExportCreateStory(TestCausalityLinks.Instance);
        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var cut = Render<CausalityLineageView>(ps => ps.Add(c => c.Model, lineage));

        var links = cut.FindAll("a.ln-link");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(links, Is.Not.Empty);
            Assert.That(links.Select(a => a.TextContent.Trim()), Is.All.EqualTo("Follow"));
            Assert.That(links.Select(a => a.GetAttribute("href")), Is.All.StartWith("test:item:"));
        }
    }

    [Test]
    public void Render_LaterRunsAndACohortsMembers_LinkThroughThePolicy()
    {
        var lineage = BuildImportLineage(CompleteCascade(), links: TestCausalityLinks.Instance);
        var cut = Render<CausalityLineageView>(ps => ps.Add(c => c.Model, lineage));

        foreach (var toggle in cut.FindAll(".ln-members-toggle").ToList())
            toggle.Click();

        var itemLinks = cut.FindAll("a.ln-link").Where(a => a.TextContent.Trim() != "View Pending Export"
                                                            && a.TextContent.Trim() != "View Pending Exports").ToList();
        var memberLinks = cut.FindAll(".ln-member a.ln-link");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(memberLinks, Is.Not.Empty);
            Assert.That(itemLinks.Select(a => a.TextContent.Trim()), Is.All.EqualTo("Follow"));
            Assert.That(itemLinks.Select(a => a.GetAttribute("href")), Is.All.StartWith("test:item:"));
        }
    }

    [Test]
    public void Render_TimelinesSourceRecord_LinksThroughThePolicy()
    {
        var context = CausalityTestData.NewJoinerContext() with { Links = TestCausalityLinks.Instance };
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), context);

        var cut = Render<CausalityTimelineView>(ps => ps.Add(c => c.Model, model));

        var recordChip = cut.FindComponents<ObjectChip>().First(c => c.Instance.Kind == ObjectChipKind.ConnectedSystemObject);
        Assert.That(recordChip.Instance.Href, Is.EqualTo($"test:cso:1:{CausalityTestData.CsoId}"));
    }

    /// <summary>
    /// Without a policy of its own, the Lineage is the item page's, word for word.
    /// </summary>
    [Test]
    public void Render_ItemPagesLineage_SaysViewActivityItem()
    {
        var (model, chain) = CausalityLinkPolicyTests.ExportCreateStory(links: null);
        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var cut = Render<CausalityLineageView>(ps => ps.Add(c => c.Model, lineage));

        var links = cut.FindAll("a.ln-link");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(links.Select(a => a.TextContent.Trim()), Is.All.EqualTo("View activity item"));
            Assert.That(links.Select(a => a.GetAttribute("href")), Is.All.StartWith("/activity/item/"));
        }
    }
}
