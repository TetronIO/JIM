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
}
