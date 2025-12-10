namespace JIM.Models.Core.DTOs;

/// <summary>
/// Lightweight representation of a MetaverseObjectType for list views.
/// </summary>
public class MetaverseObjectTypeHeader
{
    public int Id { get; set; }

    /// <summary>
    /// The singular name of the object type (e.g., "User", "Group").
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The plural name of the object type for display in list views (e.g., "Users", "Groups").
    /// </summary>
    public string PluralName { get; set; } = null!;

    public DateTime Created { get; set; }
    public int AttributesCount { get; set; }
    public bool BuiltIn { get; set; }
    public bool HasPredefinedSearches { get; set; }
    public MetaverseObjectDeletionRule DeletionRule { get; set; }
    public int? DeletionGracePeriodDays { get; set; }

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
            HasPredefinedSearches = entity.PredefinedSearches?.Count > 0,
            DeletionRule = entity.DeletionRule,
            DeletionGracePeriodDays = entity.DeletionGracePeriodDays
        };
    }
}