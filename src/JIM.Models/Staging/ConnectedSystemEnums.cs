namespace JIM.Models.Staging
{
    public enum ConnectedSystemObjectStatus
    {
        Normal = 0,
        Obsolete = 1
    }

    public enum ConnectedSystemObjectJoinType
    {
        /// <summary>
        /// Default state, the Connector Space Object is not joined to a Metaverse Object.
        /// </summary>
        NotJoined = 0,
        /// <summary>
        /// The Connector Space Object was projected to the Metaverse, resulting in a Metaverse Object being created.
        /// </summary>
        Projected = 1,
        /// <summary>
        /// A sync rule required a Metaverse Object to be provisioned to the Connector space, resulting in a Connector Space Object being created.
        /// </summary>
        Provisioned = 2,
        /// <summary>
        /// If the CSO and MVO both existed and were joined, then they were just err, joined, i.e. not as the result of being projected, or provisioned.
        /// This is common when a connector space/connector space object is cleared down, objects re-imported and synchronised and joins re-established. In this scenario, the original project/provision metadata is lost.
        /// </summary>
        Joined = 3
    }

    public enum ConnectedSystemSettingCategory
    {
        Connectivity,
        General,
        Capabilities,
        Schema
    }

    public enum ConnectedSystemSettingType
    {
        String,
        StringEncrypted,
        Integer,
        Heading,
        Label,
        Text,
        DropDown,
        CheckBox,
        Divider,
        File
    }

    public enum ConnectedSystemImportObjectChangeType
    {
        NotSet,
        Add,
        Update,
        Delete
    }

    public enum ConnectedSystemImportObjectError
    {
        NotSet,
        /// <summary>
        /// We were unable to determine what type of object was returned from the connected system.
        /// </summary>
        CouldNotDetermineObjectType,
        /// <summary>
        /// The attribute used to uniquely identify the object could not be found on the object we got from the connected system.
        /// </summary>
        MissingUniqueIdentifierAttribute,
        /// <summary>
        /// There's been an issue with the configuration of JIM and import cannot complete.
        /// </summary>
        ConfigurationError
    }

    public enum ConnectedSystemRunType
    {
        FullImport = 0,
        DeltaImport = 1,
        FullSynchronisation = 2,
        DeltaSynchronisation = 3,
        Export = 4
    }
}
