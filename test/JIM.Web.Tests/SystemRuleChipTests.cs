// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers SystemRuleChip (#399): a Connected System and the Synchronisation Rule a value came through, as one pill
/// with a CS glyph before the system's name and an SR glyph before the rule's, each half linking on its own.
/// </summary>
[TestFixture]
public class SystemRuleChipTests : JimComponentTestContext
{
    [Test]
    public void SystemRuleChip_WithBothHrefs_RendersOnePillWithAGlyphAndLinkPerHalf()
    {
        var cut = Render<SystemRuleChip>(p => p
            .Add(c => c.ConnectedSystemName, "HR")
            .Add(c => c.ConnectedSystemHref, "/admin/connected-systems/4")
            .Add(c => c.SyncRuleName, "HR Import Users")
            .Add(c => c.SyncRuleHref, "/admin/sync-rules/9"));

        var links = cut.FindAll("a.jim-system-rule-chip-part");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".jim-object-chip-glyph").Select(g => g.TextContent), Is.EqualTo(new[] { "CS", "SR" }));
            Assert.That(links.Select(a => a.GetAttribute("href")), Is.EqualTo(new[] { "/admin/connected-systems/4", "/admin/sync-rules/9" }));
            Assert.That(links.Select(a => a.GetAttribute("aria-label")),
                Is.EqualTo(new[] { "Connected System HR", "Synchronisation Rule HR Import Users" }));
        }
    }

    [Test]
    public void SystemRuleChip_WithNoHrefs_RendersBothHalvesUnlinked()
    {
        var cut = Render<SystemRuleChip>(p => p
            .Add(c => c.ConnectedSystemName, "HR")
            .Add(c => c.SyncRuleName, "HR Import Users"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("a"), Is.Empty);
            Assert.That(cut.FindAll(".jim-object-chip-name").Select(n => n.TextContent), Is.EqualTo(new[] { "HR", "HR Import Users" }));
            Assert.That(cut.Find(".jim-system-rule-chip").GetAttribute("title"),
                Is.EqualTo("Connected System: HR. Synchronisation Rule: HR Import Users"));
        }
    }

    [Test]
    public void SystemRuleChip_RuleDeleted_KeepsTheSystemAndSaysTheRuleIsGoneWithoutALink()
    {
        var cut = Render<SystemRuleChip>(p => p
            .Add(c => c.ConnectedSystemName, "Facilities")
            .Add(c => c.ConnectedSystemHref, "/admin/connected-systems/4")
            .Add(c => c.SyncRuleDeleted, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-system-rule-chip-deleted").TextContent, Does.Contain("rule deleted"));
            Assert.That(cut.FindAll("a").Select(a => a.GetAttribute("href")), Is.EqualTo(new[] { "/admin/connected-systems/4" }));
        }
    }

    [Test]
    public void SystemRuleChip_NoRule_RendersTheSystemHalfAlone()
    {
        var cut = Render<SystemRuleChip>(p => p.Add(c => c.ConnectedSystemName, "HR"));

        Assert.That(cut.FindAll(".jim-object-chip-glyph").Select(g => g.TextContent), Is.EqualTo(new[] { "CS" }));
    }
}
