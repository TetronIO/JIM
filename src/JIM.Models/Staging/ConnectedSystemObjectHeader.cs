namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectHeader
    {
        #region accessors
        public Guid Id { get; set; }

        public string? DisplayName { get; set; }

        public ConnectedSystemObjectAttributeValue? ExternalIdAttributeValue { get; set; }

        public ConnectedSystemObjectAttributeValue? SecondaryExternalIdAttributeValue { get; set; }

        public DateTime Created { get; set; }

        public DateTime? LastUpdated { get; set; }

        public int TypeId { get; set; }

        public string TypeName { get; set; } = string.Empty;

        public ConnectedSystemObjectStatus Status { get; set; }

        /// <summary>
        /// How was this CSO joined to an MVO, if at all?
        /// </summary>
        public ConnectedSystemObjectJoinType JoinType { get; set; }

        /// <summary>
        /// When this Connector Space Object was joined to the Metaverse.
        /// </summary>
        public DateTime? DateJoined { get; set; }
        #endregion

        #region constructors
        public ConnectedSystemObjectHeader()
        {
            Created = DateTime.UtcNow;
            Status = ConnectedSystemObjectStatus.Normal;
            JoinType = ConnectedSystemObjectJoinType.NotJoined;
        }
        #endregion
    }
}
