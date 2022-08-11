namespace JIM.Models.Search.Dto
{
    public class PredefinedSearchHeader
    {
        public int Id { get; set; }
        public string MetaverseObjectTypeName { get; set; }
        public bool IsDefaultForMetaverseObjectType { get; set; }
        public string Name { get; set; }
        public string Uri { get; set; }
        public bool BuiltIn { get; set; }
        public int MetaverseAttributeCount { get; set; }
        public DateTime Created { get; set; }
    }
}
