// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using MudBlazor;

namespace JIM.Web.Causality;

/// <summary>
/// The single source of truth for how every <see cref="ActivityRunProfileExecutionItemSyncOutcomeType"/>
/// value is displayed: plain-language label, technical label, tone and icon. The Helpers outcome-type
/// methods delegate here so existing callers keep identical behaviour, and the causality visualisation
/// builds on the same mapping.
/// </summary>
public static class OutcomeDisplayMap
{
    private static readonly Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, OutcomeDisplay> Map = new()
    {
        // Import outcomes
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded] =
            new OutcomeDisplay("Record added", "CSO Added", CausalityTone.Success, Icons.Material.Filled.Add),
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated] =
            new OutcomeDisplay("Record updated", "CSO Updated", CausalityTone.Info, Icons.Material.Filled.Edit),
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted] =
            new OutcomeDisplay("Record deleted", "CSO Deleted", CausalityTone.Error, Icons.Material.Filled.Delete),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected] =
            new OutcomeDisplay("Deletion detected", "CSO Deletion Detected", CausalityTone.Warning, Icons.Material.Filled.RemoveCircle),

        // Import outcomes; confirming import (export confirmation)
        [ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed] =
            new OutcomeDisplay("Export confirmed", "CSO Export Confirmed", CausalityTone.Success, Icons.Material.Filled.CheckCircle),
        [ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed] =
            new OutcomeDisplay("Export failed", "CSO Export Failed", CausalityTone.Error, Icons.Material.Filled.Cancel),

        // Sync outcomes; inbound
        [ActivityRunProfileExecutionItemSyncOutcomeType.Projected] =
            new OutcomeDisplay("Identity created", "MVO Projected", CausalityTone.Primary, Icons.Material.Filled.AirlineStops),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Joined] =
            new OutcomeDisplay("Joined to Identity", "CSO Joined", CausalityTone.Secondary, Icons.Material.Filled.Link),
        [ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow] =
            new OutcomeDisplay("Attributes flowed", "MVO Attribute Flow", CausalityTone.Secondary, Icons.Material.Filled.SyncAlt),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected] =
            new OutcomeDisplay("Disconnected", "CSO Disconnected", CausalityTone.Warning, Icons.Material.Filled.LinkOff),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope] =
            new OutcomeDisplay("Left scope", "Out of Scope", CausalityTone.Warning, Icons.Material.Filled.FilterAltOff),
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted] =
            new OutcomeDisplay("Identity deleted", "MVO Deleted", CausalityTone.Error, Icons.Material.Filled.PersonRemove),
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled] =
            new OutcomeDisplay("Identity deletion scheduled", "MVO Deletion Scheduled", CausalityTone.Warning, Icons.Material.Filled.HourglassBottom),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection] =
            new OutcomeDisplay("Drift corrected", "CSO Drift Corrected", CausalityTone.Warning, Icons.Material.Filled.CompareArrows),

        // Sync outcomes; outbound (Pending Export creation during sync)
        [ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned] =
            new OutcomeDisplay("Provisioned", "CSO Provisioned", CausalityTone.Primary, Icons.Material.Filled.SwitchAccessShortcut),
        [ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated] =
            new OutcomeDisplay("Export queued", "CSO Pending Export", CausalityTone.Info, Icons.Material.Filled.Schedule),

        // Export execution outcomes
        [ActivityRunProfileExecutionItemSyncOutcomeType.Exported] =
            new OutcomeDisplay("Exported", "CSO Exported", CausalityTone.Info, Icons.Material.Filled.Output),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned] =
            new OutcomeDisplay("Deprovisioned", "CSO Deprovisioned", CausalityTone.Error, Icons.Material.Filled.CloudOff),

        // Attribute priority (#91): a deliberate blank assertion, and a value cleared with no
        // surviving contributor; both worth drawing the eye to.
        [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] =
            new OutcomeDisplay("Blank asserted", "MVO Null Asserted", CausalityTone.Warning, Icons.Material.Filled.DoNotDisturbOn),
        [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] =
            new OutcomeDisplay("Value cleared", "MVO No Contributor", CausalityTone.Warning, Icons.Material.Filled.HighlightOff)
    };

    /// <summary>
    /// Gets the display mapping for a sync outcome type. Every enum value is mapped; an unmapped
    /// value (a future enum addition without a mapping here) falls back to the enum name so the UI
    /// still renders rather than throwing.
    /// </summary>
    public static OutcomeDisplay Get(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return Map.TryGetValue(outcomeType, out var display)
            ? display
            : new OutcomeDisplay(outcomeType.ToString(), outcomeType.ToString(), CausalityTone.Secondary, Icons.Material.Filled.Circle);
    }

    /// <summary>
    /// Maps a causality tone onto the corresponding MudBlazor palette colour.
    /// </summary>
    public static Color ToMudBlazorColor(CausalityTone tone)
    {
        return tone switch
        {
            CausalityTone.Primary => Color.Primary,
            CausalityTone.Success => Color.Success,
            CausalityTone.Info => Color.Info,
            CausalityTone.Warning => Color.Warning,
            CausalityTone.Error => Color.Error,
            CausalityTone.Secondary => Color.Secondary,
            _ => Color.Default
        };
    }
}
