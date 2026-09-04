// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the identity strip on a Synchronisation Rule's page: the one place the rule's Connected System,
/// direction and object types are stated. The behaviour worth pinning is the direction: the row this replaced
/// drew a fixed arrow from the Metaverse type to the Connected System type, so an Inbound rule read as though it
/// exported. The strip keeps the Metaverse on the left and the Connected System on the right, as the create form
/// does, and turns the arrow to face the way data flows.
/// </summary>
[TestFixture]
public class SyncRuleIdentityStripTests : JimComponentTestContext
{
    [Test]
    public void SyncRuleIdentityStrip_ExportRule_PointsTheArrowAtTheConnectedSystem()
    {
        var cut = RenderStrip(SyncRuleDirection.Export);

        var flow = cut.Find(".jim-identity-flow");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flow.ClassList, Does.Contain("jim-identity-flow-outbound"));
            Assert.That(flow.TextContent.Trim(), Is.EqualTo("Outbound"));
            // The arrowhead is the last thing in the flow, so it sits against the Connected System chip.
            Assert.That(flow.LastElementChild!.ClassList, Does.Contain("jim-identity-flow-head"));
        }
    }

    [Test]
    public void SyncRuleIdentityStrip_ImportRule_PointsTheArrowAtTheMetaverse()
    {
        var cut = RenderStrip(SyncRuleDirection.Import);

        var flow = cut.Find(".jim-identity-flow");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flow.ClassList, Does.Contain("jim-identity-flow-inbound"));
            Assert.That(flow.TextContent.Trim(), Is.EqualTo("Inbound"));
            // Reversed: the arrowhead leads, against the Metaverse chip.
            Assert.That(flow.FirstElementChild!.ClassList, Does.Contain("jim-identity-flow-head"));
        }
    }

    [Test]
    public void SyncRuleIdentityStrip_EitherDirection_KeepsTheMetaverseTypeOnTheLeft()
    {
        // The create form fixes the Metaverse type on the left and the Connected System type on the right, and
        // only its arrow changes with the direction; the strip must agree with it, or the same rule reads two
        // ways on two tabs.
        foreach (var direction in new[] { SyncRuleDirection.Export, SyncRuleDirection.Import })
        {
            var cut = RenderStrip(direction);

            var chips = cut.FindComponents<ObjectChip>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(chips, Has.Count.EqualTo(2), $"{direction}: expected a chip per side");
                Assert.That(chips[0].Instance.Kind, Is.EqualTo(ObjectChipKind.Metaverse), $"{direction}: left chip");
                Assert.That(chips[0].Instance.TypeName, Is.EqualTo("User"));
                Assert.That(chips[1].Instance.Kind, Is.EqualTo(ObjectChipKind.ConnectedSystem), $"{direction}: right chip");
                Assert.That(chips[1].Instance.TypeName, Is.EqualTo("inetOrgPerson"));
            }
        }
    }

    [Test]
    public void SyncRuleIdentityStrip_Always_LinksToTheConnectedSystem()
    {
        var cut = RenderStrip(SyncRuleDirection.Export);

        var link = cut.Find("a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.GetAttribute("href"), Is.EqualTo("/admin/connected-systems/7"));
            Assert.That(link.TextContent, Does.Contain("Corporate Directory"));
        }
    }

    [Test]
    public void SyncRuleIdentityStrip_Container_IsAnOutlinedPaperLikeEveryOtherPanel()
    {
        // The strip is a panel of the page, so it takes the design system's radius and border from MudPaper
        // rather than carrying a pill shape of its own; the first cut did, and it was sent back for exactly that.
        var cut = RenderStrip(SyncRuleDirection.Export);

        var paper = cut.FindComponent<MudPaper>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(paper.Instance.Outlined, Is.True);
            Assert.That(paper.Find("div").ClassList, Does.Contain("jim-identity-strip"));
        }
    }

    private IRenderedComponent<SyncRuleIdentityStrip> RenderStrip(SyncRuleDirection direction) =>
        Render<SyncRuleIdentityStrip>(p => p
            .Add(c => c.Direction, direction)
            .Add(c => c.MetaverseObjectTypeName, "User")
            .Add(c => c.ConnectedSystemObjectTypeName, "inetOrgPerson")
            .Add(c => c.ConnectedSystemId, 7)
            .Add(c => c.ConnectedSystemName, "Corporate Directory"));
}
