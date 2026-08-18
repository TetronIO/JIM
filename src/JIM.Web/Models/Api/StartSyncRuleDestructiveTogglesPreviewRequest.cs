// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// The destructive Synchronisation Rule toggles to preview (#1115): what the proposed Outbound Deprovision Action
/// and Inbound Out-of-Scope Action would do to the objects the rule stands over, before either is saved.
/// </summary>
public class StartSyncRuleDestructiveTogglesPreviewRequest
{
    /// <summary>
    /// The proposed action for a joined target object whose Metaverse Object leaves this export rule's scope:
    /// Disconnect leaves the object in the target Connected System; Delete stages a Delete export that removes it.
    /// Omitted or null previews the stored rule's action, matching the update endpoint's semantics exactly.
    /// Read only by export Synchronisation Rules.
    /// </summary>
    public OutboundDeprovisionAction? OutboundDeprovisionAction { get; set; }

    /// <summary>
    /// The proposed action for a joined Connected System Object that leaves this import rule's scope or is
    /// obsoleted: RemainJoined keeps the join ("once managed, always managed"); Disconnect breaks it, recalls what
    /// the object contributed and can trigger the Metaverse Object's deletion rules. Omitted or null previews the
    /// stored rule's action, matching the update endpoint's semantics exactly. Read only by import
    /// Synchronisation Rules.
    /// </summary>
    public InboundOutOfScopeAction? InboundOutOfScopeAction { get; set; }

    /// <summary>
    /// Whether drill-down rows are kept in full or capped per summary group. Counts are exact either way; capping
    /// bounds only what is retained for drill-down. Defaults to Capped, the recommended choice for large
    /// populations.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;
}
