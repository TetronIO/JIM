// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One Metaverse Object that a proposed configuration would disconnect a Connected System Object from, carrying
/// everything its type's deletion rule is evaluated against (#1251).
/// </summary>
/// <remarks>
/// The fields are exactly what <c>ISyncEngine.EvaluateMvoDeletionRule</c> reads, and they are carried rather than
/// re-read so the preview can put the same question to the engine that a synchronisation run would. The rule is
/// intricate (two trigger modes, a fallback when the authoritative-source rule has no sources, an exemption for
/// internal objects) and a second implementation of it inside the preview would eventually disagree with the engine
/// about whether an object dies.
/// </remarks>
/// <param name="Id">The Metaverse Object.</param>
/// <param name="DisplayName">Its name as it is now, snapshotted for display. Personal data.</param>
/// <param name="TypeId">Its Metaverse Object Type, which the summary groups by.</param>
/// <param name="TypeName">That type's name, snapshotted for display.</param>
/// <param name="Origin">Whether it is a projected object or an internal one, which is protected from deletion.</param>
/// <param name="DeletionRule">The type's deletion rule.</param>
/// <param name="DeletionTriggerMode">Whether one authoritative source disconnecting is enough, or all must have gone.</param>
/// <param name="DeletionGracePeriod">How long deletion would wait; null or zero means immediately.</param>
/// <param name="DeletionTriggerConnectedSystemIds">The type's authoritative sources.</param>
/// <param name="JoinedConnectedSystemIds">
/// The Connected System of every Connected System Object joined to this object, one entry per object rather than
/// per system. The duplication is deliberate and load-bearing: the engine counts remaining connectors at object
/// level, so a system holding two joined objects must contribute two entries or a disconnection would look like the
/// last one when it is not.
/// </param>
public record MetaverseObjectDisconnectionCandidate(
    Guid Id,
    string? DisplayName,
    int TypeId,
    string TypeName,
    MetaverseObjectOrigin Origin,
    MetaverseObjectDeletionRule DeletionRule,
    AuthoritativeSourceTriggerMode DeletionTriggerMode,
    TimeSpan? DeletionGracePeriod,
    IReadOnlyList<int> DeletionTriggerConnectedSystemIds,
    IReadOnlyList<int> JoinedConnectedSystemIds);
