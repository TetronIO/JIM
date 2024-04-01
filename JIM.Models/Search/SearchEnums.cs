namespace JIM.Models.Search
{
    public enum SearchGroupType
    {
        All = 0,
        Any = 1
    }

    /// <summary>
    /// The comparison to be made on the search term in the search query.
    /// The values need to map to operations that the database can natively perform. This is why Functions/Expressions are not used.
    /// </summary>
    public enum SearchComparisonType
    {
        NotSet = 0,
        Equals = 1,
        NotEquals = 2,
        StartsWith = 3,
        NotStartsWith = 4,
        EndsWith = 5,
        NotEndsWith = 6,
        Contains = 7,
        NotContains = 8,
        LessThan = 9,
        LessThanOrEquals = 10,
        GreaterThan = 11,
        GreaterThanOrEquals = 12
    }
}
