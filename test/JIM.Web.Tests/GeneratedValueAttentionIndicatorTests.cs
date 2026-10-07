// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The generated values attention indicator on the Synchronisation Rules and Connected Systems lists (Unique Value
/// Generation, #242, release 4, Phase 9): shown only while a value waits on a decision, counting those, and linking to the
/// Generated Values tab filtered to the rule or system it sits on.
/// </summary>
[TestFixture]
public class GeneratedValueAttentionIndicatorTests : JimComponentTestContext
{
    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    [Test]
    public void Render_NotYetLoaded_RendersNothing()
    {
        var cut = Render<GeneratedValueAttentionIndicator>(p => p.Add(c => c.Attention, null).Add(c => c.SyncRuleId, 10));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void Render_OnlyAllowedRenames_RendersNothing()
    {
        var cut = Render<GeneratedValueAttentionIndicator>(p => p
            .Add(c => c.Attention, new GeneratedValueDecisionAttention { RenameAllowedCount = 2 })
            .Add(c => c.SyncRuleId, 10));

        Assert.That(cut.Markup.Trim(), Is.Empty, "an allowed rename waits on the next export, not on anyone");
    }

    [Test]
    public void Render_ValuesNeedADecisionOnARule_CountsThemAndLinksToTheTabFilteredToTheRule()
    {
        var cut = Render<GeneratedValueAttentionIndicator>(p => p
            .Add(c => c.Attention, new GeneratedValueDecisionAttention { NeedsDecisionCount = 2, RenameAllowedCount = 1 })
            .Add(c => c.SyncRuleId, 10));

        var link = cut.Find("a[data-testid='generated-value-attention']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.TextContent, Does.Contain("2").And.Not.Contain("3"));
            Assert.That(link.GetAttribute("href"), Is.EqualTo("/admin/operations?t=generated-values&syncRuleId=10"));
        }
    }

    [Test]
    public void Render_ValuesNeedADecisionOnASystem_LinksToTheTabFilteredToTheSystem()
    {
        var cut = Render<GeneratedValueAttentionIndicator>(p => p
            .Add(c => c.Attention, new GeneratedValueDecisionAttention { NeedsDecisionCount = 1 })
            .Add(c => c.ConnectedSystemId, 4));

        Assert.That(cut.Find("a[data-testid='generated-value-attention']").GetAttribute("href"),
            Is.EqualTo("/admin/operations?t=generated-values&connectedSystemId=4"));
    }
}
