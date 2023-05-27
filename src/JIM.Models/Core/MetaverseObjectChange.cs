namespace JIM.Models.Core
{
    /// <summary>
    /// Represents a change to a Metaverse Object, i.e. what was changed, when and by what/whom.
    /// </summary>
    public class MetaverseObjectChange
    {
        public Guid Id { get; set; }

        /// <summary>
        /// What Metaverse Object does this change relate to?
        /// Will be null if the operation was DELETE.
        /// </summary>
        public MetaverseObject? MetaverseObject { get; set; }

        /// <summary>
        /// When was this change made?
        /// </summary>
        public DateTime ChangeMade { get; set; }

        /// <summary>
        /// Which user initiated this change, if any?
        /// </summary>
        public MetaverseObject? ChangeMadeBy { get; set; }

        public MetaverseObjectChangeInitiator ChangeInitiator { get; set; } 

        // todo: add in links to workflow instance, group and sync rules for the initiators...

        public List<MetaverseObjectChangeItem> ChangeItems { get; set; } = new List<MetaverseObjectChangeItem>();
    }
}
