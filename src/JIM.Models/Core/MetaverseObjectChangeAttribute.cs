namespace JIM.Models.Core
{
    public class MetaverseObjectChangeAttribute
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The parent for this metaverse object change item.
        /// </summary>
        public MetaverseObjectChange MetaverseObjectChange { get; set; }

        public MetaverseAttribute Attribute { get; set; }

        /// <summary>
        /// A list of what values were added to this attribute.
        /// </summary>
        public List<MetaverseObjectChangeAttributeValue> ValuesAdded { get; set; } = new List<MetaverseObjectChangeAttributeValue>();

        /// <summary>
        /// A list of what values were renmoved from this attribute.
        /// </summary>
        public List<MetaverseObjectChangeAttributeValue> ValuesRemoved { get; set; } = new List<MetaverseObjectChangeAttributeValue>();
    }
}
