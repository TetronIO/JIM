// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// Names the generation and correction events in a generated attribute's history (Unique Value Generation, #242, release
/// 4, Phase 9), so the attribute inspector's rail reads "Value generated" and "Value corrected" rather than a bare Added
/// or Set. A correction is recognised by its change record: Collision Remediation writes the value outside any
/// synchronisation, as the System, under its own initiator name.
/// </summary>
public static class GeneratedValueHistoryDisplay
{
    /// <summary>The event <paramref name="entry"/> was, or null when it is neither a generation nor a correction.</summary>
    public static GeneratedValueHistoryEvent? EventFor(AttributeHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.IsGeneratedValue || entry.Kind == AttributeHistoryChangeKind.Removed)
            return null;

        return entry.Change.ChangeInitiatorType == MetaverseObjectChangeInitiatorType.System
               && entry.Change.InitiatedByName == MetaverseServer.CollisionRemediationInitiatorName
            ? GeneratedValueHistoryEvent.Corrected
            : GeneratedValueHistoryEvent.Generated;
    }
}
