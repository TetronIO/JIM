namespace JIM.Models.Search;

public class PredefinedSearchCriteriaGroup
{
    public int Id { get; set; }

    public SearchGroupType Type { get; set; }

    public List<PredefinedSearchCriteria> Criteria { get; set; } = new();

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