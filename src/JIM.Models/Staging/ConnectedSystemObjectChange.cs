namespace JIM.Models.Staging
{
    /// <summary>
    /// Represents a change to a Connected System Object, i.e. what was changed, when and how.
    /// </summary>
    public class ConnectedSystemObjectChange
    {
        public Guid Id { get; set; }

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
        public DateTime ChangeMade { get; set; }
        
        /// <summary>
        /// What caused this change.
        /// Acceptable values: imports and synchronisations.
        /// </summary>
        public ConnectedSystemRunType RunType { get; set; }

        /// <summary>
        /// What was the change type?
        /// Acceptable values: UPDATE and DELETE. There would be no change entry for create.
        /// </summary>
        public ConnectedSystemImportObjectChangeType ChangeType { get; set; }

        /// <summary>
        /// A list of what was changed. Multiple changes can be recorded at once.
        /// </summary>
        public List<ConnectedSystemObjectChangeItem> ChangeItems { get; set; } = new List<ConnectedSystemObjectChangeItem>();
    }
}