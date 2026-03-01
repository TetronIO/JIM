namespace JIM.Models.Activities;

/// <summary>
/// The type of outcome recorded in an RPEI sync outcome node.
/// Covers all three run profile types: import, sync, and export.
/// </summary>
public enum ActivityRunProfileExecutionItemSyncOutcomeType
{
    // Import outcomes
    CsoAdded,
    CsoUpdated,
    CsoDeleted,

    // Import outcomes — confirming import (export confirmation)
    ExportConfirmed,
    ExportFailed,

    // Sync outcomes — inbound
    Projected,
    Joined,
    AttributeFlow,
    Disconnected,
    DisconnectedOutOfScope,
    MvoDeleted,

    // Sync outcomes — outbound (pending export creation during sync)
    Provisioned,
    PendingExportCreated,

    // Export execution outcomes
    Exported,
    Deprovisioned
}

/// <summary>
/// Controls how much detail is recorded for sync outcome graphs on each RPEI.
/// Higher levels provide richer audit trails but increase storage usage.
/// </summary>
public enum ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel
{
    /// <summary>
    /// No outcome tree — RPEI ObjectChangeType only (legacy behaviour).
    /// Maximum performance, minimal storage.
    /// </summary>
    None,

    /// <summary>
    /// Root-level outcomes only (Projected, Joined, Exported, etc.) — no nested children.
    /// Enables stat chips on list view with basic causal visibility.
    /// </summary>
    Standard,

    /// <summary>
    /// Full tree with nested children (Projected -> AttributeFlow -> PendingExportCreated per system).
    /// Default. Full audit trail, debugging, compliance.
    /// </summary>
    Detailed
}
