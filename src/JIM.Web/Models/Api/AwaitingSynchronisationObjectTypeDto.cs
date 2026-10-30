// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Connected System Object Type whose objects the Connected System's next synchronisation, Delta or Full, will all
/// process, because configuration changes saved since it last did so can change what they produce. There is one entry per
/// Object Type, however many changes are waiting behind it. Other Object Types are processed as usual.
/// </summary>
public class AwaitingSynchronisationObjectTypeDto
{
    /// <summary>
    /// The unique identifier of the Connected System Object Type.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The name of the Connected System Object Type.
    /// </summary>
    public string ObjectTypeName { get; set; } = string.Empty;

    /// <summary>
    /// How many saved changes are waiting for the type. This is the true number even when the list of changes is shorter.
    /// </summary>
    public int ChangeCount { get; set; }

    /// <summary>
    /// When the oldest of those changes was saved (UTC): how long the type has been waiting.
    /// </summary>
    public DateTime EarliestSavedAt { get; set; }

    /// <summary>
    /// How many Connected System Objects of the type the next synchronisation will process; null when it was not counted.
    /// </summary>
    public int? ObjectCount { get; set; }

    /// <summary>
    /// The changes waiting for the type, newest first. At most 500 changes are listed across the Connected System, so a
    /// system saved to a great many times stays light to read; <c>ChangeCount</c> is the full count.
    /// </summary>
    public List<AwaitingSynchronisationChangeDto> Changes { get; set; } = new();

    /// <summary>
    /// Creates a DTO from the application-layer entry.
    /// </summary>
    public static AwaitingSynchronisationObjectTypeDto FromEntry(ObjectTypeAwaitingSynchronisation entry)
    {
        return new AwaitingSynchronisationObjectTypeDto
        {
            ConnectedSystemObjectTypeId = entry.ConnectedSystemObjectTypeId,
            ObjectTypeName = entry.ObjectTypeName,
            ChangeCount = entry.ChangeCount,
            EarliestSavedAt = entry.EarliestRequestedAt,
            ObjectCount = entry.ObjectCount,
            Changes = entry.Changes.Select(AwaitingSynchronisationChangeDto.FromSummary).ToList()
        };
    }
}
