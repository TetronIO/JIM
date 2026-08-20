// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Utilities;
using JIM.Web.Causality;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Exhaustive coverage of <see cref="OutcomeDisplayMap"/>: every
/// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType"/> value must have a complete
/// display mapping (plain label, technical label, tone and icon) with no default-case gaps.
/// </summary>
[TestFixture]
public class OutcomeDisplayMapTests
{
    /// <summary>
    /// The expected display mapping for every outcome type. Technical labels and icons must match the
    /// behaviour of the pre-existing Helpers methods exactly (captured before the delegation refactor).
    /// </summary>
    private static readonly (ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string PlainLabel, string TechnicalLabel, CausalityTone Tone, string Icon)[] ExpectedMappings =
    [
        (ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded, "Record added", "CSO Added", CausalityTone.Success, Icons.Material.Filled.Add),
        (ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, "Record updated", "CSO Updated", CausalityTone.Info, Icons.Material.Filled.Edit),
        (ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted, "Record deleted", "CSO Deleted", CausalityTone.Error, Icons.Material.Filled.Delete),
        (ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected, "Deletion detected", "CSO Deletion Detected", CausalityTone.Warning, Icons.Material.Filled.RemoveCircle),
        (ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, "Export confirmed", "CSO Export Confirmed", CausalityTone.Success, Icons.Material.Filled.CheckCircle),
        (ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed, "Export failed", "CSO Export Failed", CausalityTone.Error, Icons.Material.Filled.Cancel),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Projected, "Identity created", "MVO Projected", CausalityTone.Primary, Icons.Material.Filled.AirlineStops),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Joined, "Joined to Identity", "CSO Joined", CausalityTone.Secondary, Icons.Material.Filled.Link),
        (ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, "Attributes flowed", "MVO Attribute Flow", CausalityTone.Secondary, Icons.Material.Filled.SyncAlt),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, "Disconnected", "CSO Disconnected", CausalityTone.Warning, Icons.Material.Filled.LinkOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope, "Left scope", "Out of Scope", CausalityTone.Warning, Icons.Material.Filled.FilterAltOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted, "Identity deleted", "MVO Deleted", CausalityTone.Error, Icons.Material.Filled.PersonRemove),
        (ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, "Drift corrected", "CSO Drift Corrected", CausalityTone.Warning, Icons.Material.Filled.CompareArrows),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, "Provisioned", "CSO Provisioned", CausalityTone.Primary, Icons.Material.Filled.SwitchAccessShortcut),
        (ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, "Export queued", "CSO Pending Export", CausalityTone.Info, Icons.Material.Filled.Schedule),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Exported, "Exported", "CSO Exported", CausalityTone.Info, Icons.Material.Filled.Output),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, "Deprovisioned", "CSO Deprovisioned", CausalityTone.Error, Icons.Material.Filled.CloudOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled, "Identity deletion scheduled", "MVO Deletion Scheduled", CausalityTone.Warning, Icons.Material.Filled.HourglassBottom),
        (ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull, "Blank asserted", "MVO Null Asserted", CausalityTone.Warning, Icons.Material.Filled.DoNotDisturbOn),
        (ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, "Value cleared", "MVO No Contributor", CausalityTone.Warning, Icons.Material.Filled.HighlightOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, "Enters import scope", "Would Fall In Scope", CausalityTone.Info, Icons.Material.Filled.FilterAlt),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, "Leaves import scope", "Would Fall Out Of Scope", CausalityTone.Warning, Icons.Material.Filled.FilterAltOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, "Becomes eligible for deletion", "Would Become Deletion Eligible", CausalityTone.Error, Icons.Material.Filled.DeleteOutline),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible, "No longer eligible for deletion", "Would Cease To Be Deletion Eligible", CausalityTone.Success, Icons.Material.Filled.RestoreFromTrash),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate, "Deletion date changes", "Would Change Deletion Eligible Date", CausalityTone.Warning, Icons.Material.Filled.EditCalendar),
        (ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued, "Deprovision queued", "CSO Pending Delete", CausalityTone.Error, Icons.Material.Filled.AutoDelete),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, "Disconnects from its Metaverse Object", "Would Disconnect From Metaverse Object", CausalityTone.Warning, Icons.Material.Filled.LinkOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, "Removed from the target system", "Would Stage Delete Export", CausalityTone.Error, Icons.Material.Filled.AutoDelete),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined, "Keeps its Metaverse Object join", "Would Remain Joined", CausalityTone.Success, Icons.Material.Filled.Link),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction, "Scope-exit action changes", "Would Change Deprovision Action", CausalityTone.Warning, Icons.Material.Filled.SwapHoriz),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow, "Attribute Flow does not evaluate", "Would Fail Attribute Flow", CausalityTone.Error, Icons.Material.Filled.RuleFolder),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject, "Joins a different Metaverse Object", "Would Join Different Metaverse Object", CausalityTone.Error, Icons.Material.Filled.SwapHoriz),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject, "Joins instead of projecting", "Would Join Instead Of Project", CausalityTone.Success, Icons.Material.Filled.Link),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin, "Projects instead of joining", "Would Project Instead Of Join", CausalityTone.Error, Icons.Material.Filled.CallSplit),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously, "Matches more than one Metaverse Object", "Would Match Ambiguously", CausalityTone.Warning, Icons.Material.Filled.QuestionMark),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting, "No longer creates an identity", "Would Stop Projecting", CausalityTone.Warning, Icons.Material.Filled.PersonOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning, "No longer creates an account", "Would Stop Provisioning", CausalityTone.Warning, Icons.Material.Filled.NoAccounts),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift, "Free to drift from JIM", "Would Stop Correcting Drift", CausalityTone.Warning, Icons.Material.Filled.SyncDisabled),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported, "Stops being imported, stays joined", "Would Stop Being Imported", CausalityTone.Warning, Icons.Material.Filled.CloudOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported, "Imported again", "Would Resume Being Imported", CausalityTone.Success, Icons.Material.Filled.CloudSync),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues, "Contributed values withdrawn", "Would Withdraw Contributed Values", CausalityTone.Warning, Icons.Material.Filled.Undo),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues, "Contributed values kept", "Would Retain Contributed Values", CausalityTone.Info, Icons.Material.Filled.Inventory2)
    ];

    /// <summary>
    /// The preview transitions (#827) are the one group whose plain label is what an administrator reads on a
    /// decision screen, so they are held to a stricter shape than the run outcomes beside them: present tense, and
    /// no "Would" prefix. The panel's own heading already establishes that nothing has happened yet, and repeating
    /// it on every row cost a column's worth of width to say nothing.
    /// </summary>
    private static readonly ActivityRunProfileExecutionItemSyncOutcomeType[] PreviewTransitions =
    [
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues
    ];

    [Test]
    public void Get_PreviewTransitions_PlainLabelsReadAsWrittenEnglish()
    {
        foreach (var outcomeType in PreviewTransitions)
        {
            var plainLabel = OutcomeDisplayMap.Get(outcomeType).PlainLabel;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(plainLabel, Does.Not.StartWith("Would"),
                    $"Plain label for {outcomeType} still carries the redundant \"Would\" prefix");
                Assert.That(plainLabel, Is.Not.EqualTo(outcomeType.ToString().SplitOnCapitalLetters()),
                    $"Plain label for {outcomeType} is the de-PascalCased enum name rather than written English");
            }
        }
    }

    [Test]
    public void Get_EveryOutcomeType_HasACompleteMapping()
    {
        foreach (var outcomeType in Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>())
        {
            var display = OutcomeDisplayMap.Get(outcomeType);

            Assert.That(display, Is.Not.Null, $"No mapping for {outcomeType}");
            Assert.That(display.PlainLabel, Is.Not.Empty, $"Empty plain label for {outcomeType}");
            Assert.That(display.TechnicalLabel, Is.Not.Empty, $"Empty technical label for {outcomeType}");
            Assert.That(display.Icon, Is.Not.Empty, $"Empty icon for {outcomeType}");
        }
    }

    [Test]
    public void Get_ExpectedMappingsTable_CoversEveryEnumValue()
    {
        var expectedTypes = ExpectedMappings.Select(m => m.OutcomeType).ToList();
        var allTypes = Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>();

        Assert.That(expectedTypes, Is.EquivalentTo(allTypes),
            "The expected mappings table must cover every outcome type exactly once");
    }

    [Test]
    public void Get_EveryOutcomeType_ReturnsExpectedLabelsToneAndIcon()
    {
        foreach (var (outcomeType, plainLabel, technicalLabel, tone, icon) in ExpectedMappings)
        {
            var display = OutcomeDisplayMap.Get(outcomeType);

            Assert.That(display.PlainLabel, Is.EqualTo(plainLabel), $"Plain label mismatch for {outcomeType}");
            Assert.That(display.TechnicalLabel, Is.EqualTo(technicalLabel), $"Technical label mismatch for {outcomeType}");
            Assert.That(display.Tone, Is.EqualTo(tone), $"Tone mismatch for {outcomeType}");
            Assert.That(display.Icon, Is.EqualTo(icon), $"Icon mismatch for {outcomeType}");
        }
    }

    [Test]
    public void Get_EveryOutcomeType_LabelsContainNoEmDashes()
    {
        foreach (var outcomeType in Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>())
        {
            var display = OutcomeDisplayMap.Get(outcomeType);

            Assert.That(display.PlainLabel, Does.Not.Contain('—'), $"Em dash in plain label for {outcomeType}");
            Assert.That(display.TechnicalLabel, Does.Not.Contain('—'), $"Em dash in technical label for {outcomeType}");
        }
    }

    [TestCase(CausalityTone.Primary, Color.Primary)]
    [TestCase(CausalityTone.Success, Color.Success)]
    [TestCase(CausalityTone.Info, Color.Info)]
    [TestCase(CausalityTone.Warning, Color.Warning)]
    [TestCase(CausalityTone.Error, Color.Error)]
    [TestCase(CausalityTone.Secondary, Color.Secondary)]
    public void ToMudBlazorColor_EveryTone_MapsToMatchingColour(CausalityTone tone, Color expectedColour)
    {
        Assert.That(OutcomeDisplayMap.ToMudBlazorColor(tone), Is.EqualTo(expectedColour));
    }
}
