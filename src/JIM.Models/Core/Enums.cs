namespace JIM.Models.Core
{
    public enum AttributeDataType
    {
        String = 0,
        Number = 1,
        DateTime = 2,
        Binary = 3,
        Reference = 4,
        Guid = 5,
        Bool = 6
    }

    public enum AttributePlurality
    {
        SingleValued = 0,
        MultiValued = 1
    }

    public enum QueryRange
    {
        Forever,
        LastYear,
        LastMonth,
        LastWeek
    }

    public enum QuerySortBy
    {
        DateCreated,
        Name
    }

    public enum BuiltInObjectTypeNames
    {
        User,
        Group
    }
}