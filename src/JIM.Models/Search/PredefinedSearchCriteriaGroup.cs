namespace JIM.Models.Search;

/// <summary>
/// A logical group of search criteria combined using either All (AND) or Any (OR) logic.
/// Groups can be nested to construct complex queries.
/// </summary>
public class PredefinedSearchCriteriaGroup
{
    public int Id { get; set; }

    /// <summary>
    /// Determines how criteria within this group are combined: All (AND) or Any (OR).
    /// </summary>
    public SearchGroupType Type { get; set; }

    /// <summary>
    /// The individual search criteria within this group.
    /// </summary>
    public List<PredefinedSearchCriteria> Criteria { get; set; } = new();

    /// <summary>
    /// The display order of this group relative to its siblings.
    /// </summary>
    public int Position { get; set; } = 0;

    /// <summary>
    /// PredefinedSearchCriteriaGroups can be nested, to enable more complex queries to be constructed, i.e. ANY(ALL(x=1,y=2),ANY(c=1,d=1))
    /// </summary>
    public List<PredefinedSearchCriteriaGroup> ChildGroups { get; set; } = new();

    /// <summary>
    /// Navigation property for child groups
    /// </summary>
    public PredefinedSearchCriteriaGroup? ParentGroup { get; set; }
}