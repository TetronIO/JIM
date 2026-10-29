// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The decisions a Synchronisation Rule save makes about existing objects (#2615), asked by the save and by the
/// acknowledgement before it: whether the rule's Object Type is recorded as awaiting synchronisation, and whether the
/// Metaverse Objects of an export rule's type are flagged for review. The shape of the facts they are asked is tested here;
/// that the dialog and the save then agree is <see cref="ConfigurationChangeEffectAgreementTests"/>.
/// </summary>
[TestFixture]
public class ConfigurationChangeSynchronisationDecisionTests
{
    private static readonly ConfigurationChangeCaptureOutcome ConsequentialChange =
        ConfigurationChangeCaptureOutcome.Changed(ConfigurationChangeClass.SyncAffecting, ["Scope"], ["objectScopingCriteriaGroups"]);

    private static SyncRule Rule(SyncRuleDirection direction, bool enabled = true) => new()
    {
        Id = 5,
        Direction = direction,
        Enabled = enabled,
        ProvisionToConnectedSystem = true,
        OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
    };

    private static SyncRuleScopeState StateOf(SyncRule rule) => new(
        rule.Enabled, rule.ProvisionToConnectedSystem == true, SyncRuleScopingProposal.FromCurrentScope(rule), rule.OutboundDeprovisionAction);

    // -- RecordsAwaitingSynchronisation --------------------------------------------------------------------------------

    [TestCase(SyncRuleDirection.Import, true, true)]
    [TestCase(SyncRuleDirection.Import, false, false)]
    [TestCase(SyncRuleDirection.Export, true, false)]
    [TestCase(SyncRuleDirection.Export, false, false)]
    public void RecordsAwaitingSynchronisation_ConsequentialChange_IsAnImportRuleThatIsOn(SyncRuleDirection direction, bool enabled, bool expected)
    {
        Assert.That(ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisation(Rule(direction, enabled), ConsequentialChange), Is.EqualTo(expected));
    }

    [Test]
    public void RecordsAwaitingSynchronisation_ImportRuleSwitchedOff_Records()
    {
        var outcome = ConfigurationChangeCaptureOutcome.Changed(ConfigurationChangeClass.SyncAffecting, ["Enabled"], ["enabled"]);

        Assert.That(ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisation(Rule(SyncRuleDirection.Import, enabled: false), outcome), Is.True);
    }

    [Test]
    public void RecordsAwaitingSynchronisation_ExportRule_NeverRecords()
    {
        Assert.That(ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisation(
            Rule(SyncRuleDirection.Export), ConfigurationChangeCaptureOutcome.NotTracked), Is.False);
    }

    // -- RecordsAwaitingSynchronisationForSimpleModeType ---------------------------------------------------------------

    [Test]
    public void RecordsAwaitingSynchronisationForSimpleModeType_ConsequentialChangeToATypeAnEnabledImportRuleCovers_Records()
    {
        Assert.That(ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisationForSimpleModeType(ConsequentialChange, enabledImportRuleCoversType: true), Is.True);
    }

    [Test]
    public void RecordsAwaitingSynchronisationForSimpleModeType_ATypeNoEnabledImportRuleCovers_DoesNotRecord()
    {
        // Nothing synchronises the objects of such a type, so there is nothing to process.
        Assert.That(ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisationForSimpleModeType(ConsequentialChange, enabledImportRuleCoversType: false), Is.False);
    }

    [Test]
    public void RecordsAwaitingSynchronisationForSimpleModeType_CosmeticChange_DoesNotRecord()
    {
        var outcome = ConfigurationChangeCaptureOutcome.Changed(ConfigurationChangeClass.Cosmetic, ["Name"], ["name"]);

        Assert.That(ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisationForSimpleModeType(outcome, enabledImportRuleCoversType: true), Is.False);
    }

    [Test]
    public void RecordsAwaitingSynchronisationForSimpleModeType_SystemCaptureThatFoundNoEarlierVersion_StillRecords()
    {
        // The capture compares the whole Connected System, so "created" says nothing about whether the matching rule is
        // new: the matching rule save is what is being recorded, and it is in force.
        Assert.That(ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisationForSimpleModeType(
            ConfigurationChangeCaptureOutcome.Created, enabledImportRuleCoversType: true), Is.True);
    }

    // -- FlagsExportReview ---------------------------------------------------------------------------------------------

    [Test]
    public void FlagsExportReview_ImportRule_NeverFlags()
    {
        var rule = Rule(SyncRuleDirection.Import);

        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(rule, isNewRule: true, storedState: null), Is.False);
    }

    [Test]
    public void FlagsExportReview_NewEnabledExportRule_Flags()
    {
        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(Rule(SyncRuleDirection.Export), isNewRule: true, storedState: null), Is.True);
    }

    [Test]
    public void FlagsExportReview_NewDisabledExportRule_DoesNotFlag()
    {
        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(Rule(SyncRuleDirection.Export, enabled: false), isNewRule: true, storedState: null), Is.False);
    }

    [Test]
    public void FlagsExportReview_StoredStateThatCannotBeRead_Flags()
    {
        // The rule vanished between the read and the save: a needless review costs time, a missed one leaves objects wrong.
        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(Rule(SyncRuleDirection.Export), isNewRule: false, storedState: null), Is.True);
    }

    [Test]
    public void FlagsExportReview_SavedExactlyAsStored_DoesNotFlag()
    {
        var rule = Rule(SyncRuleDirection.Export);

        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(rule, isNewRule: false, StateOf(rule)), Is.False);
    }

    [Test]
    public void FlagsExportReview_RuleSwitchedOn_Flags()
    {
        var stored = StateOf(Rule(SyncRuleDirection.Export, enabled: false));

        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(Rule(SyncRuleDirection.Export), isNewRule: false, stored), Is.True);
    }

    [Test]
    public void FlagsExportReview_RuleSwitchedOff_DoesNotFlag()
    {
        var stored = StateOf(Rule(SyncRuleDirection.Export));

        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(Rule(SyncRuleDirection.Export, enabled: false), isNewRule: false, stored), Is.False,
            "neither disabling a rule nor switching provisioning off destroys what already exists");
    }

    [Test]
    public void FlagsExportReview_ProvisioningSwitchedOn_Flags()
    {
        var storedRule = Rule(SyncRuleDirection.Export);
        storedRule.ProvisionToConnectedSystem = false;
        var saved = Rule(SyncRuleDirection.Export);

        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(saved, isNewRule: false, StateOf(storedRule)), Is.True);
    }

    [Test]
    public void FlagsExportReview_DeprovisioningActionChanged_Flags()
    {
        var stored = StateOf(Rule(SyncRuleDirection.Export));
        var saved = Rule(SyncRuleDirection.Export);
        saved.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(saved, isNewRule: false, stored), Is.True);
    }

    [Test]
    public void FlagsExportReview_DeprovisioningActionChangedAlongsideSwitchingTheRuleOff_DoesNotFlag()
    {
        // A disabled rule deprovisions nothing; re-enabling it is a review of its own.
        var stored = StateOf(Rule(SyncRuleDirection.Export));
        var saved = Rule(SyncRuleDirection.Export, enabled: false);
        saved.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        Assert.That(ConfigurationChangeSynchronisationPolicy.FlagsExportReview(saved, isNewRule: false, stored), Is.False);
    }

    [Test]
    public void ExportReviewSectionKeys_NameExactlyTheSectionsThatFlag()
    {
        // The acknowledgement says which changed sections the review carries by these keys. They are the sections of the
        // rule's snapshot that SyncRuleScopeState reads: each is one the decision turns on, and the list is the whole of
        // them (there is a test per setting above).
        Assert.That(ConfigurationChangeSynchronisationPolicy.ExportReviewSectionKeys,
            Is.EquivalentTo(new[] { "enabled", "provisionToConnectedSystem", "objectScopingCriteriaGroups", "outboundDeprovisionAction" }));
    }
}
