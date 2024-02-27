namespace JIM.Models.Search
{
    public enum PredefinedSearchGroupType
    {
        All = 0,
        Any = 1
    }

    /// <summary>
    /// The comparison to be made on the search term in the search query.
    /// The values need to map to operations that the database can natively perform. This is why Functions/Expressions are not used.
    /// </summary>
    public enum PredefinedSearchComparisonType
    {
        Equals = 0,
        NotEquals = 1,
        StartsWith = 2,
        NotStartsWith = 3,
        EndsWith = 4,
        NotEndsWith = 5,
        Contains = 6,
        NotContains = 7,
        LessThan = 8,
        LessThanOrEquals = 9,
        GreaterThan = 10,
        GreaterThanOrEquals = 11
    }
}
