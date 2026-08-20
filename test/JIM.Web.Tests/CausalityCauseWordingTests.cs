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

    /// <summary>
    /// A cohort carrying a reason, with or without the Connected System whose chip the reason reads on from.
    /// </summary>
    private static CausalChainCohort ReasonCohort(CausalReasonCode code, bool withConnectedSystem)
    {
        return new CausalChainCohort
        {
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ReasonCode = code,
            ConnectedSystemId = withConnectedSystem ? 4 : null,
            ConnectedSystemName = withConnectedSystem ? "Yellowstone APAC" : null,
            Members = [new CausalChainMember { DisplayName = "Tina Adams" }]
        };
    }

    [TestCase(CausalReasonCode.LastConnectorDisconnected,
        "held the last remaining connection, so the Deletion Rule deleted them")]
    [TestCase(CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured,
        "held the last remaining connection, so the Deletion Rule deleted them (no authoritative sources were configured)")]
    [TestCase(CausalReasonCode.AllAuthoritativeSourcesDisconnected,
        "was the last authoritative source to disconnect, so the Deletion Rule deleted them")]
    [TestCase(CausalReasonCode.AuthoritativeSourceDisconnected,
        "was an authoritative source and disconnected, so the Deletion Rule deleted them")]
    public void Reason_WithAConnectedSystem_ContinuesTheSentenceTheChipStarts(CausalReasonCode code, string expected)
    {
        Assert.That(CausalityCauseWording.Reason(ReasonCohort(code, withConnectedSystem: true)), Is.EqualTo(expected));
    }

    [TestCase(CausalReasonCode.LastConnectorDisconnected,
        "The last remaining connection was removed, so the Deletion Rule deleted them")]
    [TestCase(CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured,
        "The last remaining connection was removed, so the Deletion Rule deleted them (no authoritative sources were configured)")]
    [TestCase(CausalReasonCode.AllAuthoritativeSourcesDisconnected,
        "The last authoritative source disconnected, so the Deletion Rule deleted them")]
    [TestCase(CausalReasonCode.AuthoritativeSourceDisconnected,
        "An authoritative source disconnected, so the Deletion Rule deleted them")]
    public void Reason_WithNoConnectedSystem_SuppliesItsOwnSubject(CausalReasonCode code, string expected)
    {
        Assert.That(CausalityCauseWording.Reason(ReasonCohort(code, withConnectedSystem: false)), Is.EqualTo(expected));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Reason_NotSet_SaysNothingRatherThanGuessing(bool withConnectedSystem)
    {
        Assert.That(CausalityCauseWording.Reason(ReasonCohort(CausalReasonCode.NotSet, withConnectedSystem)), Is.Null);
    }

    [TestCase(CausalReasonCode.LastConnectorDisconnected)]
    [TestCase(CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured)]
    [TestCase(CausalReasonCode.AllAuthoritativeSourcesDisconnected)]
    [TestCase(CausalReasonCode.AuthoritativeSourceDisconnected)]
    public void Reason_EveryCode_ReadsAsAClauseAfterTheChipAndAsASentenceWithout(CausalReasonCode code)
    {
        // The chip renders immediately before the phrase, so with a Connected System the phrase is the
        // predicate of a sentence the chip is the subject of and must open with its verb; without one the
        // phrase stands alone and has to name its own subject. Getting either capital wrong is the whole
        // defect this wording replaced: a fragment with nothing to attach to.
        var continuing = CausalityCauseWording.Reason(ReasonCohort(code, withConnectedSystem: true))!;
        var standalone = CausalityCauseWording.Reason(ReasonCohort(code, withConnectedSystem: false))!;

        Assert.Multiple(() =>
        {
            Assert.That(char.IsLower(continuing[0]), Is.True, "a phrase following the chip continues its sentence");
            Assert.That(char.IsUpper(standalone[0]), Is.True, "a phrase standing alone opens its own sentence");
            Assert.That(continuing, Does.Contain("the Deletion Rule deleted them"),
                "the row's relevance is that it explains the deletion, so it has to say so");
            Assert.That(standalone, Does.Contain("the Deletion Rule deleted them"));
        });
    }

    [TestCase(CausalChainResolution.Resolved, null)]
    [TestCase(CausalChainResolution.NoFurtherCauses, "End of the recorded causality chain")]
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

    #region the synchronisation that queued an export (#1223)

    private static CausalChainCohort QueueingCohort(
        string? displayName = "Project-AgileCore",
        CausalReasonCode reasonCode = CausalReasonCode.NotSet)
    {
        return new CausalChainCohort
        {
            EdgeType = CausalEdgeType.PendingExportQueueingCausedExportExecution,
            ReasonCode = reasonCode,
            ConnectedSystemId = 4,
            ConnectedSystemName = "Glitterband EMEA",
            Members = [new CausalChainMember { DisplayName = displayName }]
        };
    }

    /// <summary>
    /// The sentence an export item leads with. It has to answer "why did this run change anything", which is
    /// the question an export item could not previously answer at all.
    /// </summary>
    [Test]
    public void Sentence_QueueingCohort_NamesTheSynchronisationThatStagedTheChange()
    {
        var sentence = CausalityCauseWording.Sentence(QueueingCohort(), effectName: null);

        Assert.That(Read(sentence), Is.EqualTo(
            "A synchronisation of Project-AgileCore staged this change, and this run exported it"));
    }

    /// <summary>
    /// An export staged before the cause was recorded, or against an object whose name was never snapshotted,
    /// still states what happened rather than falling silent on a blank name.
    /// </summary>
    [Test]
    public void Sentence_QueueingCohortWithNoName_StillStatesWhatHappened()
    {
        var sentence = CausalityCauseWording.Sentence(QueueingCohort(displayName: null), effectName: null);

        Assert.That(Read(sentence), Is.EqualTo(
            "A synchronisation of 1 object staged this change, and this run exported it"));
    }

    /// <summary>
    /// A provisioning create leads with the decision and answers create-versus-update in the verb. The system
    /// is named in the sentence, so the hop must not also render its chip; see ShowConnectedSystemChip below.
    /// </summary>
    [Test]
    public void Sentence_QueueingCohortForACreate_LeadsWithTheProvisioningDecision()
    {
        var cohort = QueueingCohort("Mia Young (S8-352)", CausalReasonCode.ExportCreateStaged);

        var sentence = CausalityCauseWording.Sentence(cohort, effectName: null);

        Assert.That(Read(sentence), Is.EqualTo(
            "Mia Young (S8-352) was provisioned to Glitterband EMEA, so this run created the record"));
    }

    [Test]
    public void Sentence_QueueingCohortForAnUpdate_LeadsWithTheIdentityChange()
    {
        var cohort = QueueingCohort("Sam Scott (S8-198)", CausalReasonCode.ExportUpdateStaged);

        var sentence = CausalityCauseWording.Sentence(cohort, effectName: null);

        Assert.That(Read(sentence), Is.EqualTo(
            "Sam Scott (S8-198)'s Identity changed, so this run applied the changes to the record"));
    }

    /// <summary>
    /// The flagship case: an account being removed leads with the Identity's deletion, whose own causes
    /// continue above it.
    /// </summary>
    [Test]
    public void Sentence_QueueingCohortForADelete_LeadsWithTheIdentityDeletion()
    {
        var cohort = QueueingCohort("Tina Adams (S8-999)", CausalReasonCode.ExportDeleteStaged);

        var sentence = CausalityCauseWording.Sentence(cohort, effectName: null);

        Assert.That(Read(sentence), Is.EqualTo(
            "The Identity Tina Adams (S8-999) was deleted, so this run deleted the record"));
    }

    /// <summary>
    /// The provisioning rule reads on from its own chip as the subject, matching the deletion reasons'
    /// chip-as-subject grammar.
    /// </summary>
    [Test]
    public void Reason_QueueingCohortForACreateNamingARule_ReadsOnFromTheRuleChip()
    {
        var cohort = new CausalChainCohort
        {
            EdgeType = CausalEdgeType.PendingExportQueueingCausedExportExecution,
            ReasonCode = CausalReasonCode.ExportCreateStaged,
            ConnectedSystemId = 4,
            ConnectedSystemName = "Glitterband EMEA",
            SyncRuleId = 12,
            SyncRuleName = "EMEA LDAP Export Users",
            Members = [new CausalChainMember { DisplayName = "Mia Young (S8-352)" }]
        };

        Assert.That(CausalityCauseWording.Reason(cohort), Is.EqualTo("made the provisioning decision"));
    }

    [Test]
    public void Reason_QueueingCohortForAnUpdate_HasNothingToAdd()
    {
        Assert.That(CausalityCauseWording.Reason(
            QueueingCohort("Sam Scott (S8-198)", CausalReasonCode.ExportUpdateStaged)), Is.Null);
    }

    /// <summary>
    /// The queueing hop's sentence names the system exported to, so rendering its chip as well would restate
    /// the page's own system with no role beside it: the unattributed-token shape the attribution row was
    /// redesigned to remove. Every other seam keeps the chip, whose reason phrase it is the subject of.
    /// </summary>
    [Test]
    public void ShowConnectedSystemChip_QueueingCohort_SuppressesTheChip()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CausalityCauseWording.ShowConnectedSystemChip(QueueingCohort()), Is.False);
            Assert.That(CausalityCauseWording.ShowConnectedSystemChip(new CausalChainCohort
            {
                EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                ConnectedSystemId = 1,
                ConnectedSystemName = "Yellowstone APAC"
            }), Is.True);
        });
    }

    #endregion
}
