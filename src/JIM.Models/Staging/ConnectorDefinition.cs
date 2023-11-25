namespace JIM.Models.Staging
{
    public class ConnectorDefinition
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public string? Url { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastUpdated { get; set; }
        /// <summary>
        /// Is this a Connector built-in to JIM itself, or third-party supplied?
        /// </summary>
        public bool BuiltIn { get; set; }
        public List<ConnectorDefinitionFile> Files { get; }
        public List<ConnectorDefinitionSetting> Settings { get; set; }
        /// <summary>
        /// Backwards navigation link for EF.
        /// </summary>
        public List<ConnectedSystem>? ConnectedSystems { get; set; }        

        public ConnectorDefinition() 
        {
            Files = new List<ConnectorDefinitionFile>();
            Settings = new List<ConnectorDefinitionSetting>();
            Created = DateTime.UtcNow;
        }

        #region Capabilities
        /// <summary>
        /// Does the Connector support receiving full imports? i.e. receiving the total representation of all objects in the connected system.
        /// Most should, to enable reconcilation after exports, though some might just be drop-exports, i.e. for when connectivity to connected systems is not bi-directional.
        /// </summary>
        public bool SupportsFullImport { get; set; }

        /// <summary>
        /// Does the Connector support receiving delta imports? i.e. receiving just specific attribute/object changes for objects in the connected system.
        /// It's recommended that a Connector does support this approach where possible as this is the quickest way of receiving changes from connected systems.
        /// </summary>
        public bool SupportsDeltaImport { get; set; }

        /// <summary>
        /// Does the Connector support exporting changes/objects to the connected system? Some systems might be import-only, i.e. source-of-truth/HCM systems.
        /// It's recommended that a Connector does support exports though, to ensure that the system can be updated with attribute values it's not authoritative for, i.e. email-address, phone-numbers, etc in the case of HCM systems.
        /// </summary>
        public bool SupportsExport { get; set; }

        /// <summary>
        /// Does the Connector support the concept of partitions? Commonly, systems such as LDAP directories will. If a Connector does support Partitions, it may also support Containers, though it doesn't have to.
        /// </summary>
        public bool SupportsPartitions { get; set; }

        /// <summary>
        /// Does the Connector support the concept of containers? Containers are part of partitions.
        /// If Partition Containers are supported, then Partitions must also be supported.
        /// </summary>
        public bool SupportsPartitionContainers { get; set; }
        #endregion
    }
}
