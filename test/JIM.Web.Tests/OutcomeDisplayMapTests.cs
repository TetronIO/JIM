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
/// display mapping (one label, tone and icon) with no default-case gaps.
/// </summary>
[TestFixture]
public class OutcomeDisplayMapTests
{
    /// <summary>
    /// The expected display mapping for every outcome type.
    /// </summary>
    private static readonly (ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string Label, CausalityTone Tone, string Icon)[] ExpectedMappings =
    [
        (ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded, "Connected System Object added", CausalityTone.Success, Icons.Material.Filled.Add),
        (ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, "Connected System Object updated", CausalityTone.Info, Icons.Material.Filled.Edit),
        (ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted, "Connected System Object deleted", CausalityTone.Error, Icons.Material.Filled.Delete),
        (ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected, "Deletion detected", CausalityTone.Warning, Icons.Material.Filled.RemoveCircle),
        (ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, "Export confirmed", CausalityTone.Success, Icons.Material.Filled.CheckCircle),
        (ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed, "Export failed", CausalityTone.Error, Icons.Material.Filled.Cancel),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Projected, "Projected to the Metaverse", CausalityTone.Primary, Icons.Material.Filled.AirlineStops),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Joined, "Joined to Metaverse Object", CausalityTone.Secondary, Icons.Material.Filled.Link),
        (ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, "Attributes flowed", CausalityTone.Secondary, Icons.Material.Filled.SyncAlt),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, "Disconnected", CausalityTone.Warning, Icons.Material.Filled.LinkOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope, "Left scope", CausalityTone.Warning, Icons.Material.Filled.FilterAltOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted, "Metaverse Object deleted", CausalityTone.Error, Icons.Material.Filled.PersonRemove),
        (ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, "Drift corrected", CausalityTone.Warning, Icons.Material.Filled.CompareArrows),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, "Provisioned", CausalityTone.Primary, Icons.Material.Filled.SwitchAccessShortcut),
        (ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, "Export queued", CausalityTone.Info, Icons.Material.Filled.Schedule),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Exported, "Exported", CausalityTone.Info, Icons.Material.Filled.Output),
        (ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, "Deprovisioned", CausalityTone.Error, Icons.Material.Filled.CloudOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled, "Metaverse Object deletion scheduled", CausalityTone.Warning, Icons.Material.Filled.HourglassBottom),
        (ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled, "Metaverse Object deletion cancelled", CausalityTone.Success, Icons.Material.Filled.HourglassDisabled),
        (ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull, "Blank asserted", CausalityTone.Warning, Icons.Material.Filled.DoNotDisturbOn),
        (ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, "Value cleared", CausalityTone.Warning, Icons.Material.Filled.HighlightOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved, "Values preserved", CausalityTone.Warning, Icons.Material.Filled.AcUnit),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, "Enters import scope", CausalityTone.Info, Icons.Material.Filled.FilterAlt),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, "Leaves import scope", CausalityTone.Warning, Icons.Material.Filled.FilterAltOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, "Becomes eligible for deletion", CausalityTone.Error, Icons.Material.Filled.DeleteOutline),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible, "No longer eligible for deletion", CausalityTone.Success, Icons.Material.Filled.RestoreFromTrash),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate, "Deletion date changes", CausalityTone.Warning, Icons.Material.Filled.EditCalendar),
        (ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued, "Deprovision queued", CausalityTone.Error, Icons.Material.Filled.AutoDelete),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, "Disconnects from its Metaverse Object", CausalityTone.Warning, Icons.Material.Filled.LinkOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, "Removed from the target system", CausalityTone.Error, Icons.Material.Filled.AutoDelete),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined, "Keeps its Metaverse Object join", CausalityTone.Success, Icons.Material.Filled.Link),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction, "Scope-exit action changes", CausalityTone.Warning, Icons.Material.Filled.SwapHoriz),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow, "Attribute Flow does not evaluate", CausalityTone.Error, Icons.Material.Filled.RuleFolder),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject, "Joins a different Metaverse Object", CausalityTone.Error, Icons.Material.Filled.SwapHoriz),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject, "Joins instead of projecting", CausalityTone.Success, Icons.Material.Filled.Link),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin, "Projects instead of joining", CausalityTone.Error, Icons.Material.Filled.CallSplit),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously, "Matches more than one Metaverse Object", CausalityTone.Warning, Icons.Material.Filled.QuestionMark),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting, "No longer creates a Metaverse Object", CausalityTone.Warning, Icons.Material.Filled.PersonOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning, "No longer creates a Connected System Object", CausalityTone.Warning, Icons.Material.Filled.NoAccounts),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift, "Free to drift from JIM", CausalityTone.Warning, Icons.Material.Filled.SyncDisabled),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported, "Stops being imported, stays joined", CausalityTone.Warning, Icons.Material.Filled.CloudOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported, "Imported again", CausalityTone.Success, Icons.Material.Filled.CloudSync),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues, "Contributed values withdrawn", CausalityTone.Warning, Icons.Material.Filled.Undo),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues, "Contributed values kept", CausalityTone.Info, Icons.Material.Filled.Inventory2),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope, "Leaves export scope, nothing to remove", CausalityTone.Info, Icons.Material.Filled.FilterAltOff),
        (ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope, "Enters export scope", CausalityTone.Info, Icons.Material.Filled.FilterAlt)
    ];

    /// <summary>
    /// The preview transitions (#827) are the one group whose label is what an administrator reads on a
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
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope
    ];

    [Test]
    public void Get_PreviewTransitions_LabelsReadAsWrittenEnglish()
    {
        foreach (var outcomeType in PreviewTransitions)
        {
            var label = OutcomeDisplayMap.Get(outcomeType).Label;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(label, Does.Not.StartWith("Would"),
                    $"Label for {outcomeType} still carries the redundant \"Would\" prefix");
                Assert.That(label, Is.Not.EqualTo(outcomeType.ToString().SplitOnCapitalLetters()),
                    $"Label for {outcomeType} is the de-PascalCased enum name rather than written English");
            }
        }
    }

    /// <summary>
    /// The export-side scope transitions exist because their import-side siblings' labels were the only ones an
    /// export rule's scope preview could show, and "Leaves import scope" against a Metaverse Object leaving an
    /// export rule names a direction the rule does not have. Their labels must say which side they are.
    /// </summary>
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope)]
    public void Get_ExportScopeTransitions_NameExportScopeAndNeverImport(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        var display = OutcomeDisplayMap.Get(outcomeType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.Label, Does.Contain("export scope"));
            Assert.That(display.Label, Does.Not.Contain("import").IgnoreCase);
            Assert.That(display.SentenceForm, Does.Contain("export scope"));
            Assert.That(display.SentenceForm, Does.Not.Contain("import").IgnoreCase);
        }
    }

    [Test]
    public void Get_EveryOutcomeType_HasACompleteMapping()
    {
        foreach (var outcomeType in Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>())
        {
            var display = OutcomeDisplayMap.Get(outcomeType);

            Assert.That(display, Is.Not.Null, $"No mapping for {outcomeType}");
            Assert.That(display.Label, Is.Not.Empty, $"Empty label for {outcomeType}");
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
    public void Get_EveryOutcomeType_ReturnsExpectedLabelToneAndIcon()
    {
        foreach (var (outcomeType, label, tone, icon) in ExpectedMappings)
        {
            var display = OutcomeDisplayMap.Get(outcomeType);

            Assert.That(display.Label, Is.EqualTo(label), $"Label mismatch for {outcomeType}");
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

            Assert.That(display.Label, Does.Not.Contain('—'), $"Em dash in label for {outcomeType}");
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
