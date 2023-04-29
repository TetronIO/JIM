namespace JIM.Models.Core
{
    public enum AttributeDataType
    {
        NotSet = 0,
        String = 1,
        Number = 2,
        DateTime = 3,
        Binary = 4,
        Reference = 5,
        Guid = 6,
        Bool = 7
    }

    public enum AttributePlurality
    {
        SingleValued = 0,
        MultiValued = 1
    }

    public enum MetaverseObjectStatus
    {
        Normal = 0,
        Obsolete = 1
    }
}