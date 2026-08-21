// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Search;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a full Predefined Search: the header fields plus the displayed attributes and the
/// criteria-group tree. Replaces the raw entity response (#1447); the target Metaverse Object Type is
/// carried as id and name, and criteria groups reuse the DTO the criteria endpoints already speak.
/// </summary>
public class PredefinedSearchDetailDto
{
    /// <summary>
    /// The unique identifier of the predefined search.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The user-facing name of the predefined search, e.g. "All Permanent Staff".
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The stable, human-readable slug used in URLs and as a search identifier.
    /// </summary>
    public string Uri { get; set; } = null!;

    /// <summary>
    /// Whether the search ships with JIM (as opposed to being administrator-defined).
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// Whether the search is currently visible to end users.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Whether this is the default search for its Metaverse Object Type.
    /// </summary>
    public bool IsDefaultForMetaverseObjectType { get; set; }

    /// <summary>
    /// The id of the Metaverse Object Type the search returns results for.
    /// </summary>
    public int MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Object Type the search returns results for.
    /// </summary>
    public string MetaverseObjectTypeName { get; set; } = null!;

    /// <summary>
    /// When the search was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The display name of the principal that created the search.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the search was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The display name of the principal that last modified the search.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The attributes surfaced as columns in the search results, ordered by Position.
    /// </summary>
    public List<PredefinedSearchAttributeDto> Attributes { get; set; } = new();

    /// <summary>
    /// The criteria groups that filter which objects match the search, ordered by Position.
    /// </summary>
    public List<PredefinedSearchCriteriaGroupDto> CriteriaGroups { get; set; } = new();

    /// <summary>
    /// Creates a DTO from an entity. The entity's MetaverseObjectType, Attributes (with their
    /// MetaverseAttribute) and CriteriaGroups graph should be populated.
    /// </summary>
    public static PredefinedSearchDetailDto FromEntity(PredefinedSearch entity)
    {
        return new PredefinedSearchDetailDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Uri = entity.Uri,
            BuiltIn = entity.BuiltIn,
            IsEnabled = entity.IsEnabled,
            IsDefaultForMetaverseObjectType = entity.IsDefaultForMetaverseObjectType,
            MetaverseObjectTypeId = entity.MetaverseObjectType.Id,
            MetaverseObjectTypeName = entity.MetaverseObjectType.Name,
            Created = entity.Created,
            CreatedByName = entity.CreatedByName,
            LastUpdated = entity.LastUpdated,
            LastUpdatedByName = entity.LastUpdatedByName,
            Attributes = entity.Attributes
                .OrderBy(a => a.Position)
                .Select(PredefinedSearchAttributeDto.FromEntity)
                .ToList(),
            CriteriaGroups = entity.CriteriaGroups
                .OrderBy(g => g.Position)
                .Select(PredefinedSearchCriteriaGroupDto.FromEntity)
                .ToList()
        };
    }
}

/// <summary>
/// API representation of one attribute surfaced as a column in a predefined search's results.
/// </summary>
public class PredefinedSearchAttributeDto
{
    /// <summary>
    /// The unique identifier of the search attribute.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The id of the Metaverse Attribute displayed in the results.
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Attribute displayed in the results.
    /// </summary>
    public string MetaverseAttributeName { get; set; } = null!;

    /// <summary>
    /// The left-to-right display order of the column; 0 is shown first.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Creates a DTO from an entity. The entity's MetaverseAttribute should be populated.
    /// </summary>
    public static PredefinedSearchAttributeDto FromEntity(PredefinedSearchAttribute entity)
    {
        return new PredefinedSearchAttributeDto
        {
            Id = entity.Id,
            MetaverseAttributeId = entity.MetaverseAttribute.Id,
            MetaverseAttributeName = entity.MetaverseAttribute.Name,
            Position = entity.Position
        };
    }
}
