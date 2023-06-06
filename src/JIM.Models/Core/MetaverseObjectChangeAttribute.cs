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
        /// A list of what values were added to or removed from this attribute.
        /// </summary>
        public List<MetaverseObjectChangeAttributeValue> ValueChanges { get; set; } = new List<MetaverseObjectChangeAttributeValue>();
    }
}
