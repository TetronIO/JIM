// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The sentences the "Caused by" chain reads back (#1223). These are plain-language claims about what
/// happened, so they are asserted as whole sentences rather than as fragments: a test that only checked
/// the parts would pass while the assembled sentence read as nonsense.
/// </summary>
[TestFixture]
public class CausalityCauseWordingTests
{
    private static string Read(IReadOnlyList<CausalityCauseSentencePart> parts)
    {
        return string.Concat(parts.Select(p => p.Text));
    }

    private static CausalChainCohort ReferenceRemovalCohort(int memberCount)
    {
        return new CausalChainCohort
        {
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ObjectTypeName = "User",
            ObjectTypePluralName = "Users",
            AttributeName = "Static Members",
            Members = Enumerable.Range(0, memberCount)
                .Select(i => new CausalChainMember { DisplayName = $"Cause {i}" })
                .ToList()
        };
    }

    [Test]
    public void Sentence_ReferenceRemovalCohortOfMany_CountsTheCausesAndNamesTheRelationship()
    {
        var cohort = ReferenceRemovalCohort(10);

        var sentence = CausalityCauseWording.Sentence(cohort, "Project Diamond");

        Assert.That(Read(sentence), Is.EqualTo(
            "10 Users were deleted, so they were removed from Project Diamond's Static Members"));
    }

    [Test]
    public void Sentence_ReferenceRemovalCohortOfOne_NamesTheCauseInsteadOfCountingIt()
    {
        var cohort = ReferenceRemovalCohort(1);
        cohort.Members[0] = new CausalChainMember { DisplayName = "Tina Adams" };

        var sentence = CausalityCauseWording.Sentence(cohort, "Project Diamond");

        Assert.That(Read(sentence), Is.EqualTo(
            "Tina Adams was deleted, so they were removed from Project Diamond's Static Members"));
    }

    [Test]
    public void Sentence_ReferenceRemovalCohortOfOneWithNoName_FallsBackToTheSingularNoun()
    {
        var cohort = ReferenceRemovalCohort(1);
        cohort.Members[0] = new CausalChainMember();

        var sentence = CausalityCauseWording.Sentence(cohort, "Project Diamond");

        Assert.That(Read(sentence), Is.EqualTo(
            "1 User was deleted, so they were removed from Project Diamond's Static Members"));
    }

    [Test]
    public void Sentence_ReferenceRemoval_HighlightsTheAttributeNameAndNothingElse()
    {
        var cohort = ReferenceRemovalCohort(10);

        var sentence = CausalityCauseWording.Sentence(cohort, "Project Diamond");

        var highlighted = sentence.Where(p => p.IsAttributeName).ToList();
        Assert.That(highlighted.Select(p => p.Text), Is.EqualTo(new[] { "Static Members" }));
    }

    [Test]
    public void Sentence_ReferenceRemovalWithNoEffectName_DropsThePossessiveRatherThanTheRelationship()
    {
        var cohort = ReferenceRemovalCohort(3);

        var sentence = CausalityCauseWording.Sentence(cohort, null);

        Assert.That(Read(sentence), Is.EqualTo(
            "3 Users were deleted, so they were removed from Static Members"));
    }

    [Test]
    public void Sentence_ReferenceRemovalWithNoAttributeName_StillStatesTheRemoval()
    {
        var cohort = ReferenceRemovalCohort(3);
        cohort = new CausalChainCohort
        {
            EdgeType = cohort.EdgeType,
            ObjectTypeName = cohort.ObjectTypeName,
            ObjectTypePluralName = cohort.ObjectTypePluralName,
            Members = cohort.Members
        };

        var sentence = CausalityCauseWording.Sentence(cohort, "Project Diamond");

        Assert.Multiple(() =>
        {
            Assert.That(Read(sentence), Is.EqualTo(
                "3 Users were deleted, so the references to them were removed from Project Diamond"));
            Assert.That(sentence.Any(p => p.IsAttributeName), Is.False);
        });
    }

    [Test]
    public void Sentence_Deprovision_ReadsAsTheDeprovisioningThisItemRecords()
    {
        var cohort = new CausalChainCohort
        {
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedDeprovision,
            ObjectTypeName = "User",
            ObjectTypePluralName = "Users",
            Members = [new CausalChainMember { DisplayName = "Tina Adams" }]
        };

        var sentence = CausalityCauseWording.Sentence(cohort, "S8-99");

        Assert.That(Read(sentence), Is.EqualTo(
            "Tina Adams was deleted, so this deprovisioning was queued"));
    }

    [Test]
    public void Sentence_ExportConfirmation_ReadsAsThisImportConfirmingTheExport()
    {
        var cohort = new CausalChainCohort
        {
            EdgeType = CausalEdgeType.ExportCausedImportConfirmation,
            ObjectTypeName = "User",
            ObjectTypePluralName = "Users",
            Members = [new CausalChainMember { DisplayName = "Tina Adams" }]
        };

        var sentence = CausalityCauseWording.Sentence(cohort, null);

        Assert.That(Read(sentence), Is.EqualTo(
            "Tina Adams was exported, and this import confirms it"));
    }

    [Test]
    public void Sentence_CohortWithNoTypeNounAtAll_StillReadsAsASentence()
    {
        var cohort = new CausalChainCohort
        {
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            AttributeName = "Static Members",
            Members = [new CausalChainMember(), new CausalChainMember()]
        };

        var sentence = CausalityCauseWording.Sentence(cohort, "Project Diamond");

        Assert.That(Read(sentence), Is.EqualTo(
            "2 objects were deleted, so they were removed from Project Diamond's Static Members"));
    }

    [TestCase(CausalReasonCode.NotSet, null)]
    [TestCase(CausalReasonCode.LastConnectorDisconnected, "Last connector disconnected")]
    [TestCase(CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured,
        "Last connector disconnected, with no authoritative sources configured")]
    [TestCase(CausalReasonCode.AllAuthoritativeSourcesDisconnected, "All authoritative sources disconnected")]
    [TestCase(CausalReasonCode.AuthoritativeSourceDisconnected, "An authoritative source disconnected")]
    public void Reason_EveryCode_HasItsOwnPhraseAndNotSetHasNone(CausalReasonCode code, string? expected)
    {
        Assert.That(CausalityCauseWording.Reason(code), Is.EqualTo(expected));
    }

    [TestCase(CausalChainResolution.Resolved, null)]
    [TestCase(CausalChainResolution.NoFurtherCauses, "End of the recorded chain")]
    [TestCase(CausalChainResolution.CauseNotRetained, "What caused this is no longer retained")]
    [TestCase(CausalChainResolution.DepthLimitReached, "More causes exist beyond this point")]
    public void Ending_EveryTerminalState_SaysSomethingDifferent(CausalChainResolution resolution, string? expected)
    {
        Assert.That(CausalityCauseWording.Ending(resolution), Is.EqualTo(expected));
    }

    [Test]
    public void MembersLabel_CountsTheCohortWithItsOwnNoun()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CausalityCauseWording.MembersLabel(ReferenceRemovalCohort(10)), Is.EqualTo("Show the 10 Users"));
            Assert.That(CausalityCauseWording.MembersLabel(ReferenceRemovalCohort(2)), Is.EqualTo("Show the 2 Users"));
            Assert.That(CausalityCauseWording.HideMembersLabel(ReferenceRemovalCohort(10)), Is.EqualTo("Hide the 10 Users"));
        });
    }
}
