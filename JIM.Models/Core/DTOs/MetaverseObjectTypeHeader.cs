namespace JIM.Models.Core.DTOs;

/// <summary>
/// Lightweight representation of a MetaverseObjectType for list views.
/// </summary>
public class MetaverseObjectTypeHeader
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
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
            Created = entity.Created,
            AttributesCount = entity.Attributes?.Count ?? 0,
            BuiltIn = entity.BuiltIn,
            HasPredefinedSearches = entity.PredefinedSearches?.Count > 0,
            DeletionRule = entity.DeletionRule,
            DeletionGracePeriodDays = entity.DeletionGracePeriodDays
        };
    }
}