// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// A Metaverse Object Type in summary, as the list returns it.
/// </summary>
public class MetaverseObjectTypeHeader
{
    /// <summary>
    /// The unique identifier of the Object Type.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The Object Type's singular name, such as User or Group.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The Object Type's plural name, such as Users or Groups.
    /// </summary>
    public string PluralName { get; set; } = null!;

    /// <summary>
    /// When the Object Type was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// How many attributes are bound to the Object Type. Counted only when the request includes child objects; 0
    /// otherwise.
    /// </summary>
    public int AttributesCount { get; set; }

    /// <summary>
    /// Whether JIM provides the Object Type. A built-in Object Type cannot be renamed or deleted, but its deletion
    /// settings can be changed.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The name of the MudBlazor icon the portal shows for the Object Type; null when none is set.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Whether any Predefined Search lists objects of this type.
    /// </summary>
    public bool HasPredefinedSearches { get; set; }

    /// <summary>
    /// When JIM deletes this type's Metaverse Objects on its own.
    /// </summary>
    public MetaverseObjectDeletionRule DeletionRule { get; set; }

    /// <summary>
    /// For the WhenAuthoritativeSourceDisconnected deletion rule, whether every selected source must disconnect
    /// before an object is deleted (AllSourcesDisconnect) or any one is enough (SpecificSourcesDisconnect). It has
    /// no effect under the other rules.
    /// </summary>
    public AuthoritativeSourceTriggerMode DeletionTriggerMode { get; set; }

    /// <summary>
    /// How long JIM waits, once the deletion rule is met, before it deletes an object; null or zero deletes at
    /// once.
    /// </summary>
    public TimeSpan? DeletionGracePeriod { get; set; }

    /// <summary>
    /// Creates a header from a MetaverseObjectType entity.
    /// </summary>
    public static MetaverseObjectTypeHeader FromEntity(MetaverseObjectType entity)
    {
        return new MetaverseObjectTypeHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            PluralName = entity.PluralName,
            Created = entity.Created,
            AttributesCount = entity.Attributes?.Count ?? 0,
            BuiltIn = entity.BuiltIn,
            Icon = entity.Icon,
            HasPredefinedSearches = entity.PredefinedSearches?.Count > 0,
            DeletionRule = entity.DeletionRule,
            DeletionTriggerMode = entity.DeletionTriggerMode,
            DeletionGracePeriod = entity.DeletionGracePeriod
        };
    }
}