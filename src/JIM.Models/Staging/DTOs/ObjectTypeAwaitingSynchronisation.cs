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
    /// The most changes listed for one Connected System, or for one Activity, however many are waiting: a system that has
    /// been saved to a great many times must not make the page that lists them heavy. The count beside the list is the
    /// true number.
    /// </summary>
    public const int MaxChangesListed = 500;

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

    /// <summary>
    /// How many Connected System Objects of the type the next synchronisation will process. Read only when the entry
    /// describes one Connected System in full; null in a status read for a list, which would otherwise pay a count per
    /// type per system.
    /// </summary>
    public int? ObjectCount { get; init; }

    /// <summary>
    /// The changes waiting behind the type, newest first. Listed only when the entry describes one Connected System in
    /// full, and then capped (see <see cref="ChangeCount"/> for the total); empty in a status read for a list.
    /// </summary>
    public IReadOnlyList<ConfigurationChangeSynchronisationSummary> Changes { get; init; } = [];
}
