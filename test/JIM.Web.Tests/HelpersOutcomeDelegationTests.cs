// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Activities;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Characterisation tests for the Helpers outcome-type display methods. The expected values below
/// were captured from the pre-refactor switch statements in Helpers.cs; after Helpers delegates to
/// OutcomeDisplayMap, every existing caller must observe identical behaviour.
/// </summary>
[TestFixture]
public class HelpersOutcomeDelegationTests
{
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded, "CSO Added")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, "CSO Updated")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted, "CSO Deleted")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected, "CSO Deletion Detected")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, "CSO Export Confirmed")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed, "CSO Export Failed")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, "MVO Projected")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, "MVO Attribute Flow")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, "CSO Joined")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, "CSO Disconnected")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope, "Out of Scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted, "MVO Deleted")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled, "MVO Deletion Scheduled")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled, "MVO Deletion Cancelled")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, "CSO Drift Corrected")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, "CSO Provisioned")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, "CSO Pending Export")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, "CSO Exported")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, "CSO Deprovisioned")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull, "MVO Null Asserted")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, "MVO No Contributor")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved, "MVO Values Preserved")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, "Would Fall In Scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, "Would Fall Out Of Scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, "Would Become Deletion Eligible")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible, "Would Cease To Be Deletion Eligible")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate, "Would Change Deletion Eligible Date")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued, "CSO Pending Delete")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, "Would Disconnect From Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, "Would Stage Delete Export")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined, "Would Remain Joined")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction, "Would Change Deprovision Action")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow, "Would Fail Attribute Flow")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject, "Would Join Different Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject, "Would Join Instead Of Project")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin, "Would Project Instead Of Join")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously, "Would Match Ambiguously")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting, "Would Stop Projecting")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning, "Would Stop Provisioning")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift, "Would Stop Correcting Drift")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported, "Would Stop Being Imported")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported, "Would Resume Being Imported")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues, "Would Withdraw Contributed Values")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues, "Would Retain Contributed Values")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope, "Would Leave Export Scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope, "Would Enter Export Scope")]
    public void GetOutcomeTypeDisplayName_EveryOutcomeType_ReturnsPreRefactorValue(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string expected)
    {
        Assert.That(Helpers.GetOutcomeTypeDisplayName(outcomeType), Is.EqualTo(expected));
    }

    // The technical label above is what the Activity and causality views want: an operator reading a run's outcomes
    // is looking for the exact outcome name. A Configuration Change Preview (#827) is read by an administrator
    // deciding whether to save, and there the plain label is the right one; these two methods exist so a surface
    // states which audience it is writing for rather than picking a label by accident.
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, "Enters import scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, "Leaves import scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, "Becomes eligible for deletion")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible, "No longer eligible for deletion")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate, "Deletion date changes")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, "Disconnects from its Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, "Removed from the target system")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined, "Keeps its Metaverse Object join")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction, "Scope-exit action changes")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow, "Attribute Flow does not evaluate")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject, "Joins a different Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin, "Projects instead of joining")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, "Identity created")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope, "Leaves export scope, nothing to remove")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope, "Enters export scope")]
    public void GetOutcomeTypePlainName_EveryOutcomeType_ReturnsThePlainLabel(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string expected)
    {
        Assert.That(Helpers.GetOutcomeTypePlainName(outcomeType), Is.EqualTo(expected));
    }

    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded, Color.Success)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, Color.Info)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, Color.Success)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, Color.Primary)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, Color.Secondary)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, Color.Secondary)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled, Color.Success)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, Color.Primary)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, Color.Info)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, Color.Info)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, Color.Info)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible, Color.Success)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined, Color.Success)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject, Color.Success)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported, Color.Success)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues, Color.Info)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope, Color.Info)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope, Color.Info)]
    public void GetOutcomeTypeMudBlazorColor_EveryOutcomeType_ReturnsPreRefactorValue(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, Color expected)
    {
        Assert.That(Helpers.GetOutcomeTypeMudBlazorColor(outcomeType), Is.EqualTo(expected));
    }

    [Test]
    public void GetOutcomeTypeIcon_EveryOutcomeType_ReturnsPreRefactorValue()
    {
        var expectedIcons = new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, string>
        {
            [ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded] = Icons.Material.Filled.Add,
            [ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated] = Icons.Material.Filled.Edit,
            [ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted] = Icons.Material.Filled.Delete,
            [ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected] = Icons.Material.Filled.RemoveCircle,
            [ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed] = Icons.Material.Filled.CheckCircle,
            [ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed] = Icons.Material.Filled.Cancel,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Projected] = Icons.Material.Filled.AirlineStops,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Joined] = Icons.Material.Filled.Link,
            [ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow] = Icons.Material.Filled.SyncAlt,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected] = Icons.Material.Filled.LinkOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope] = Icons.Material.Filled.FilterAltOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted] = Icons.Material.Filled.PersonRemove,
            [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled] = Icons.Material.Filled.HourglassBottom,
            [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled] = Icons.Material.Filled.HourglassDisabled,
            [ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection] = Icons.Material.Filled.CompareArrows,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned] = Icons.Material.Filled.SwitchAccessShortcut,
            [ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated] = Icons.Material.Filled.Schedule,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Exported] = Icons.Material.Filled.Output,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned] = Icons.Material.Filled.CloudOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] = Icons.Material.Filled.DoNotDisturbOn,
            [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] = Icons.Material.Filled.HighlightOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved] = Icons.Material.Filled.AcUnit,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope] = Icons.Material.Filled.FilterAlt,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope] = Icons.Material.Filled.FilterAltOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible] = Icons.Material.Filled.DeleteOutline,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible] = Icons.Material.Filled.RestoreFromTrash,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate] = Icons.Material.Filled.EditCalendar,
            [ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued] = Icons.Material.Filled.AutoDelete,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject] = Icons.Material.Filled.LinkOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport] = Icons.Material.Filled.AutoDelete,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined] = Icons.Material.Filled.Link,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction] = Icons.Material.Filled.SwapHoriz,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow] = Icons.Material.Filled.RuleFolder,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject] = Icons.Material.Filled.SwapHoriz,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject] = Icons.Material.Filled.Link,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin] = Icons.Material.Filled.CallSplit,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously] = Icons.Material.Filled.QuestionMark,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting] = Icons.Material.Filled.PersonOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning] = Icons.Material.Filled.NoAccounts,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift] = Icons.Material.Filled.SyncDisabled,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported] = Icons.Material.Filled.CloudOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported] = Icons.Material.Filled.CloudSync,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues] = Icons.Material.Filled.Undo,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues] = Icons.Material.Filled.Inventory2,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope] = Icons.Material.Filled.FilterAltOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope] = Icons.Material.Filled.FilterAlt
        };

        Assert.That(expectedIcons.Keys, Is.EquivalentTo(Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>()),
            "The expected icon table must cover every outcome type");

        foreach (var (outcomeType, expectedIcon) in expectedIcons)
        {
            Assert.That(Helpers.GetOutcomeTypeIcon(outcomeType), Is.EqualTo(expectedIcon),
                $"Icon mismatch for {outcomeType}");
        }
    }
}
