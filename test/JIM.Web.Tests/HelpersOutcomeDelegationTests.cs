// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Activities;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Characterisation tests for the Helpers outcome-type display methods, which delegate to
/// <see cref="JIM.Web.Causality.OutcomeDisplayMap"/>. <see cref="Helpers.GetOutcomeTypeDisplayName"/>
/// and <see cref="Helpers.GetOutcomeTypePlainName"/> used to return a technical and a plain-language
/// label respectively; the causality panel and the rest of the portal now share exactly one label per
/// outcome, so both methods return the same value. Both are asserted here so a caller of either keeps
/// seeing identical behaviour.
/// </summary>
[TestFixture]
public class HelpersOutcomeDelegationTests
{
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded, "Connected System Object added")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, "Connected System Object updated")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted, "Connected System Object deleted")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected, "Deletion detected")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, "Export confirmed")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed, "Export failed")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, "Projected to the Metaverse")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, "Attributes flowed")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, "Joined to Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, "Disconnected")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope, "Left scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin, "Left scope, join kept")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted, "Metaverse Object deleted")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled, "Metaverse Object deletion scheduled")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled, "Metaverse Object deletion cancelled")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, "Drift corrected")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, "Provisioned")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, "Export queued")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, "Exported")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, "Deprovisioned")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull, "Blank asserted")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, "Value cleared")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved, "Values preserved")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, "Enters import scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, "Leaves import scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, "Becomes eligible for deletion")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible, "No longer eligible for deletion")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate, "Deletion date changes")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued, "Deprovision queued")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, "Disconnects from its Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, "Removed from the target system")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined, "Keeps its Metaverse Object join")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction, "Scope-exit action changes")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow, "Attribute Flow does not evaluate")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject, "Joins a different Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject, "Joins instead of projecting")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin, "Projects instead of joining")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously, "Matches more than one Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting, "No longer creates a Metaverse Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning, "No longer creates a Connected System Object")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift, "Free to drift from JIM")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported, "Stops being imported, stays joined")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported, "Imported again")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues, "Contributed values withdrawn")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues, "Contributed values kept")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope, "Leaves export scope, nothing to remove")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope, "Enters export scope")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled, "Provisioning cancelled")]
    public void GetOutcomeTypeDisplayName_EveryOutcomeType_ReturnsTheOutcomesOneLabel(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string expected)
    {
        Assert.That(Helpers.GetOutcomeTypeDisplayName(outcomeType), Is.EqualTo(expected));
    }

    // GetOutcomeTypePlainName is now an alias of GetOutcomeTypeDisplayName: both delegate to the same
    // single OutcomeDisplayMap label. Kept as a separate method (and asserted separately here) so
    // existing callers (the Configuration Change Preview panel and its counts) need no change.
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
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, "Projected to the Metaverse")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope, "Leaves export scope, nothing to remove")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope, "Enters export scope")]
    public void GetOutcomeTypePlainName_EveryOutcomeType_ReturnsTheSameOneLabel(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string expected)
    {
        Assert.That(Helpers.GetOutcomeTypePlainName(outcomeType), Is.EqualTo(expected));
        Assert.That(Helpers.GetOutcomeTypePlainName(outcomeType), Is.EqualTo(Helpers.GetOutcomeTypeDisplayName(outcomeType)),
            "the two methods must never disagree: there is only one label now");
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
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin, Color.Info)]
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
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled, Color.Warning)]
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
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope] = Icons.Material.Filled.FilterAlt,
            [ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled] = Icons.Material.Filled.CancelScheduleSend,
            [ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin] = Icons.Material.Filled.FilterAlt
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
