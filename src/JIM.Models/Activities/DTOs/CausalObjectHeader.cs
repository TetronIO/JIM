// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// What the Causality Explorer (#2100) can say about an object it focuses on or a trail names: the object itself while
/// it exists, or its deletion record once it does not.
/// </summary>
/// <remarks>
/// A deleted object is still an object the explorer can focus on: its timeline is read through the records its deletion
/// left, and its way out is its deletion record, which the portal addresses by the object's own id.
/// </remarks>
public class CausalObjectHeader
{
    public CausalTimelineSubject Subject { get; init; } = null!;

    /// <summary>
    /// Whether the object has been deleted, so what is known comes from its deletion record.
    /// </summary>
    public bool IsDeleted { get; init; }

    /// <summary>
    /// When it was deleted (UTC), where it was.
    /// </summary>
    public DateTime? DeletedAt { get; init; }

    /// <summary>
    /// The object's name, where it has one: for a Connected System Object the name its Connected System gives it, for a
    /// Metaverse Object its Display Name or Common Name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// A Connected System Object's external ID.
    /// </summary>
    public string? ExternalId { get; init; }

    public string? ObjectTypeName { get; init; }

    /// <summary>
    /// A Metaverse Object Type's plural name, which the Metaverse Object's own page is addressed by.
    /// </summary>
    public string? ObjectTypePluralName { get; init; }

    /// <summary>
    /// A Connected System Object's Connected System, which its own page is addressed by.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    public string? ConnectedSystemName { get; init; }
}
