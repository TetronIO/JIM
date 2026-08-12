// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The "Caused by" chain (#1223): the upward story of why the item on screen happened. These tests cover
/// what the component decides rather than what it looks like: which causes link away, which collapse, and
/// which of the four terminal states it is showing.
/// </summary>
[TestFixture]
public class CausalityCausedByTests : JimComponentTestContext
{
    private static readonly Guid RootItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CauseItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static CausalChainMember Member(string name, Guid? itemId = null,
        CausalChainResolution resolution = CausalChainResolution.NoFurtherCauses)
    {
        return new CausalChainMember
        {
            DisplayName = name,
            RunProfileExecutionItemId = itemId,
            Resolution = resolution
        };
    }

    private static CausalChainCohort Cohort(params CausalChainMember[] members)
    {
        return new CausalChainCohort
        {
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ObjectTypeName = "User",
            ObjectTypePluralName = "Users",
            AttributeName = "Static Members",
            Members = members.ToList()
        };
    }

    private IRenderedComponent<CausalityCausedBy> RenderChain(CausalChain chain, string? effectName = "Project Diamond")
    {
        return Render<CausalityCausedBy>(p => p
            .Add(c => c.Chain, chain)
            .Add(c => c.EffectName, effectName)
            .Add(c => c.EffectItemId, RootItemId));
    }

    [Test]
    public void CausedBy_ChainWithNoCauses_RendersNothing()
    {
        var cut = RenderChain(new CausalChain { RunProfileExecutionItemId = RootItemId });

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void CausedBy_NullChain_RendersNothing()
    {
        var cut = Render<CausalityCausedBy>(p => p
            .Add(c => c.Chain, (CausalChain?)null)
            .Add(c => c.EffectItemId, RootItemId));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void CausedBy_Cohort_ReadsItsSentenceBackWithTheAttributeNameHighlighted()
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(Member("Tina Adams", CauseItemId))]
        };

        var cut = RenderChain(chain);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Find(".cb-sentence").TextContent.Trim(), Is.EqualTo(
                "Tina Adams was deleted, so they were removed from Project Diamond's Static Members"));
            Assert.That(cut.Find(".cb-attr").TextContent, Is.EqualTo("Static Members"));
        });
    }

    [Test]
    public void CausedBy_CohortOfOne_NamesTheCauseInTheSentenceRatherThanBehindADisclosure()
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(Member("Tina Adams", CauseItemId))]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.FindAll(".cb-members-toggle"), Is.Empty);
    }

    [Test]
    public void CausedBy_CohortOfMany_KeepsItsMembersCollapsedUntilAsked()
    {
        var members = Enumerable.Range(1, 10).Select(i => Member($"Cause {i}", CauseItemId)).ToArray();
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(members)]
        };

        var cut = RenderChain(chain);

        var toggle = cut.Find(".cb-members-toggle");
        Assert.Multiple(() =>
        {
            Assert.That(toggle.TextContent.Trim(), Is.EqualTo("Show the 10 Users"));
            Assert.That(cut.FindAll(".cb-member"), Is.Empty);
        });

        toggle.Click();

        Assert.That(cut.FindAll(".cb-member"), Has.Count.EqualTo(10));
    }

    [Test]
    public void CausedBy_CrossRecordCause_LinksToTheItemThatRecordedIt()
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(Member("Tina Adams", CauseItemId))]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.Find(".cb-hop a.cb-link").GetAttribute("href"),
            Is.EqualTo($"/activity/item/{CauseItemId}"));
    }

    [Test]
    public void CausedBy_CauseRecordedOnThisSameItem_DoesNotLinkBackToThePageItIsOn()
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(Member("Tina Adams", RootItemId))]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.FindAll("a.cb-link"), Is.Empty);
    }

    [Test]
    public void CausedBy_CauseWithNoRecordedItem_DoesNotLinkAnywhere()
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(Member("Tina Adams"))]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.FindAll("a.cb-link"), Is.Empty);
    }

    [Test]
    public void CausedBy_CohortWithAReasonCode_ExplainsWhyTheCauseHappened()
    {
        var cohort = Cohort(Member("Tina Adams", CauseItemId));
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts =
            [
                new CausalChainCohort
                {
                    EdgeType = cohort.EdgeType,
                    ObjectTypeName = cohort.ObjectTypeName,
                    ObjectTypePluralName = cohort.ObjectTypePluralName,
                    AttributeName = cohort.AttributeName,
                    ReasonCode = CausalReasonCode.AllAuthoritativeSourcesDisconnected,
                    Members = cohort.Members
                }
            ]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.Find(".cb-reason").TextContent.Trim(),
            Is.EqualTo("All authoritative sources disconnected"));
    }

    [Test]
    public void CausedBy_CohortNamingItsConnectedSystemAndSyncRule_ChipsBothOfThem()
    {
        var cohort = Cohort(Member("Tina Adams", CauseItemId));
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts =
            [
                new CausalChainCohort
                {
                    EdgeType = cohort.EdgeType,
                    ObjectTypeName = cohort.ObjectTypeName,
                    ObjectTypePluralName = cohort.ObjectTypePluralName,
                    AttributeName = cohort.AttributeName,
                    ConnectedSystemId = 7,
                    ConnectedSystemName = "Yellowstone APAC",
                    SyncRuleId = 3,
                    SyncRuleName = "Group Outbound",
                    Members = cohort.Members
                }
            ]
        };

        var cut = RenderChain(chain);

        var chips = cut.FindComponents<CausalityEntityChip>();
        Assert.That(chips.Select(c => c.Instance.Label),
            Is.EqualTo(new[] { "Yellowstone APAC", "Group Outbound" }));
    }

    [TestCase(CausalChainResolution.NoFurtherCauses, "End of the recorded causality chain")]
    [TestCase(CausalChainResolution.CauseNotRetained, "What caused this is no longer retained")]
    [TestCase(CausalChainResolution.DepthLimitReached, "More causes exist beyond this point")]
    public void CausedBy_TerminalCause_SaysWhyTheChainStopsThere(CausalChainResolution resolution, string expected)
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(Member("Tina Adams", CauseItemId, resolution))]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.Find(".cb-end").TextContent.Trim(), Is.EqualTo(expected));
    }

    [Test]
    public void CausedBy_ResolvedCause_RendersWhatCausedItBeneathIt()
    {
        var deeper = new CausalChainCohort
        {
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedDeprovision,
            ObjectTypeName = "User",
            ObjectTypePluralName = "Users",
            Members = [Member("Upstream HR record")]
        };
        var cause = Member("Tina Adams", CauseItemId, CausalChainResolution.Resolved);
        cause.Causes.Add(deeper);

        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(cause)]
        };

        var cut = RenderChain(chain);

        var sentences = cut.FindAll(".cb-sentence").Select(e => e.TextContent.Trim()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(sentences, Has.Count.EqualTo(2));
            Assert.That(sentences[1], Is.EqualTo(
                "Upstream HR record was deleted, so this deprovisioning was queued"));
            // Only the leaf ends the chain; a resolved cause states its own causes instead of an ending
            Assert.That(cut.FindAll(".cb-end"), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void CausedBy_NestedCohort_ReadsTheCauseAboveItAsItsOwnEffect()
    {
        var deeper = new CausalChainCohort
        {
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ObjectTypeName = "User",
            ObjectTypePluralName = "Users",
            AttributeName = "Managed By",
            Members = [Member("Ravi Patel")]
        };
        var cause = Member("Tina Adams", CauseItemId, CausalChainResolution.Resolved);
        cause.Causes.Add(deeper);

        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(cause)]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.FindAll(".cb-sentence")[1].TextContent.Trim(), Is.EqualTo(
            "Ravi Patel was deleted, so they were removed from Tina Adams's Managed By"));
    }

    [Test]
    public void CausedBy_TruncatedByDepth_SaysTheChainGoesFurtherBackThanShown()
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(Member("Tina Adams", CauseItemId, CausalChainResolution.DepthLimitReached))],
            IsTruncatedByDepth = true
        };

        var cut = RenderChain(chain);

        Assert.That(cut.FindAll(".cb-truncated"), Has.Count.EqualTo(1));
    }

    [Test]
    public void CausedBy_ChainThatEndsCleanly_DoesNotClaimToBeTruncated()
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts = [Cohort(Member("Tina Adams", CauseItemId))]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.FindAll(".cb-truncated"), Is.Empty);
    }

    [Test]
    public void CausedBy_TwoCohortsAtOneLevel_KeepsTheForkAsAFork()
    {
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = RootItemId,
            Cohorts =
            [
                Cohort(Member("Tina Adams", CauseItemId)),
                new CausalChainCohort
                {
                    EdgeType = CausalEdgeType.ExportCausedImportConfirmation,
                    ObjectTypeName = "User",
                    ObjectTypePluralName = "Users",
                    Members = [Member("Ravi Patel", CauseItemId)]
                }
            ]
        };

        var cut = RenderChain(chain);

        Assert.That(cut.FindAll(".cb-hop"), Has.Count.EqualTo(2));
    }
}
