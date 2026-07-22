// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
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
        (ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, "Value cleared", "MVO No Contributor", CausalityTone.Warning, Icons.Material.Filled.HighlightOff)
    ];

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
