namespace JIM.Models.Core
{
    public class MetaverseObjectTypeGroup
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool BuiltIn { get; set; }
        /// <summary>
        /// The order in which to show this MetaverseObjectTypeGroup in relation to others.
        /// </summary>
        public int Order { get; set; }
        public List<MetaverseObjectType> ObjectTypes { get; set; }

        public MetaverseObjectTypeGroup()
        {
            ObjectTypes = new List<MetaverseObjectType>();
        }
    }
}
