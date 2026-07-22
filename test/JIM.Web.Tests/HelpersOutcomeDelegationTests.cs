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
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, "CSO Drift Corrected")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, "CSO Provisioned")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, "CSO Pending Export")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, "CSO Exported")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, "CSO Deprovisioned")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull, "MVO Null Asserted")]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, "MVO No Contributor")]
    public void GetOutcomeTypeDisplayName_EveryOutcomeType_ReturnsPreRefactorValue(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string expected)
    {
        Assert.That(Helpers.GetOutcomeTypeDisplayName(outcomeType), Is.EqualTo(expected));
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
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, Color.Primary)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, Color.Info)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, Color.Info)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, Color.Error)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull, Color.Warning)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, Color.Warning)]
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
            [ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection] = Icons.Material.Filled.CompareArrows,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned] = Icons.Material.Filled.SwitchAccessShortcut,
            [ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated] = Icons.Material.Filled.Schedule,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Exported] = Icons.Material.Filled.Output,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned] = Icons.Material.Filled.CloudOff,
            [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] = Icons.Material.Filled.DoNotDisturbOn,
            [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] = Icons.Material.Filled.HighlightOff
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
