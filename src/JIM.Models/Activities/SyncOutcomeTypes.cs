// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Activities;

/// <summary>
/// The vocabulary rules for <see cref="ActivityRunProfileExecutionItemSyncOutcomeType"/> that more than one
/// layer needs: which outcome type reports a staged Pending Export, and which types count as one. The worker
/// writes outcomes, the repository counts them and the portal renders them, so these live here rather than in
/// any one of those layers.
/// </summary>
public static class SyncOutcomeTypes
{
    /// <summary>
    /// The outcome type that reports a staged Pending Export, chosen by what the export will actually do:
    /// a Delete deprovisions the object from the target system, everything else writes attribute values.
    ///
    /// Every staging site must go through here rather than naming
    /// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated"/> directly. A delete
    /// Pending Export carries the target's secondary external ID (the DN, for LDAP) as an attribute value
    /// change so the connector can still resolve the entry after the Connected System Object is disconnected
    /// from its Metaverse Object; reported as a plain Pending Export, that payload read as "one attribute set"
    /// and a deprovisioning cascade was indistinguishable from an ordinary attribute update.
    /// </summary>
    public static ActivityRunProfileExecutionItemSyncOutcomeType ForPendingExport(PendingExport pendingExport)
    {
        return pendingExport.ChangeType == PendingExportChangeType.Delete
            ? ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
            : ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated;
    }

    /// <summary>
    /// Whether an outcome type reports a staged Pending Export. Anything counting, filtering or linking
    /// Pending Exports on the outcome graph must accept both values: a queued deprovision is a Pending
    /// Export, and only its intent differs.
    /// </summary>
    public static bool IsPendingExport(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return outcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
            or ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued;
    }
}
