using JIM.Models.Enums;

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
        public DateTime ChangeTime { get; set; }

        /// <summary>
        /// Which user initiated this change, if any?
        /// </summary>
        public MetaverseObject? ChangeInitiator { get; set; }

        public MetaverseObjectChangeInitiatorType ChangeInitiatorType { get; set; }

        /// <summary>
        /// What was the change type?
        /// Acceptable values: UPDATE and DELETE. There would be no change object for a create scenario.
        /// </summary>
        public ObjectChangeType ChangeType { get; set; }

        // todo: add in links to workflow instance, group and sync rules for the initiators...

        /// <summary>
        /// Enables access to per-attribute value changes for the metaverse object in question.
        /// </summary>
        public List<MetaverseObjectChangeAttribute> AttributeChanges { get; set; } = new List<MetaverseObjectChangeAttribute>();
    }
}
