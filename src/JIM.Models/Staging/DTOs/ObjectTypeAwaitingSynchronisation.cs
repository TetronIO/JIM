// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A Connected System Object Type whose objects the Connected System's next synchronisation will all process, because
/// configuration changes saved since it last did so can change what they produce (#2615). One entry per Object Type,
/// however many changes are waiting behind it: the type is processed once.
/// </summary>
public class ObjectTypeAwaitingSynchronisation
{
    /// <summary>
    /// The Connected System whose next synchronisation applies the changes.
    /// </summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>
    /// The Connected System Object Type whose objects will all be processed.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; init; }

    /// <summary>
    /// The name of that Object Type.
    /// </summary>
    public string ObjectTypeName { get; init; } = string.Empty;

    /// <summary>
    /// How many saved changes are waiting to be applied to the type.
    /// </summary>
    public int ChangeCount { get; init; }

    /// <summary>
    /// When the oldest of those changes was saved (UTC): how long the type has been waiting.
    /// </summary>
    public DateTime EarliestRequestedAt { get; init; }
}
