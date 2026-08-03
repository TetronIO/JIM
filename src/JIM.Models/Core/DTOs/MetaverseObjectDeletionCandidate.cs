// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One Metaverse Object that is already on a path to automatic deletion, reduced to the facts a deletion-settings
/// preview needs about it. Everything else about the object is irrelevant to the question: given a settings pair,
/// the date JIM would delete it on is determined by its disconnection date and whether it still has connectors.
///
/// The population this describes is the objects carrying a disconnection mark. Objects without one cannot become
/// eligible under any settings, because the mark is what the housekeeping sweep looks for, so a preview that
/// widened its scope past this would be counting objects no change could affect.
/// </summary>
/// <param name="Id">The Metaverse Object.</param>
/// <param name="DisplayName">
/// Its name as it is now, snapshotted for display. Personal data: it belongs in a preview's drill-down rows and
/// never in a log line or a diagnostic message.
/// </param>
/// <param name="LastConnectorDisconnectedDate">When the object was marked as disconnected.</param>
/// <param name="HasConnectedSystemObjects">
/// Whether it still has joined Connected System Objects, which decides its fate under
/// <see cref="MetaverseObjectDeletionRule.WhenLastConnectorDisconnected"/> and is irrelevant under the
/// authoritative-source rule.
/// </param>
public record MetaverseObjectDeletionCandidate(
    Guid Id,
    string? DisplayName,
    DateTime LastConnectorDisconnectedDate,
    bool HasConnectedSystemObjects);
