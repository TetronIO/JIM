using JIM.Models.Enums;
using JIM.Models.History;

namespace JIM.Models.Staging
{
    /// <summary>
    /// Represents a change to a Connected System Object, i.e. what was changed, when and by what.
    /// </summary>
    public class ConnectedSystemObjectChange
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The connected system object change would have been caused by a synchronisation run, 
        /// though it's worth bearing in mind that sync run history can be cleared down so a reference may not always be present,
        /// depending on how old the connected system object is.
        /// </summary>
        public SyncRunHistoryDetailItem? SyncRunHistoryDetailItem { get; set; }

        /// <summary>
        /// Which Connected System did/does the Connected System Object in question relate to.
        /// Important information when the operation was DELETE and there's no ConnectedSystemObject to reference.
        /// </summary>
        public ConnectedSystem ConnectedSystem { get; set; }

        /// <summary>
        /// What Connected System Object does this change relate to?
        /// Will be null if the operation was DELETE.
        /// </summary>
        public ConnectedSystemObject? ConnectedSystemObject { get; set; }

        /// <summary>
        /// When was this change made?
        /// </summary>
        public DateTime ChangeTime { get; set; }

        /// <summary>
        /// What was the change type?
        /// Acceptable values: UPDATE and DELETE. There would be no change object for a create scenario.
        /// </summary>
        public ChangeType ChangeType { get; set; }

        /// <summary>
        /// Enables access to per-attribute value changes for the connected system object in question.
        /// </summary>
        public List<ConnectedSystemObjectChangeAttribute> AttributeChanges { get; set; } = new List<ConnectedSystemObjectChangeAttribute>();
    }
}