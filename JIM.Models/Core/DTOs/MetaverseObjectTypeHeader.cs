namespace JIM.Models.Core.DTOs
{
    public class MetaverseObjectTypeHeader
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public int AttributesCount { get; set; }
        public bool BuiltIn { get; set; }
        public bool HasPredefinedSearches { get; set; }
    }
}
