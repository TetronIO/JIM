namespace JIM.Models.Interfaces
{
    /// <summary>
    /// Defines how a Connector can let JIM know what capabilities it supports.
    /// </summary>
    public interface IConnectorCapabilities
    {
        /// <summary>
        /// Does the Connector support receiving full imports? i.e. receiving the total representation of all objects in the connected system.
        /// Most should, to enable reconcilation after exports, though some might just be drop-exports, i.e. for when connectivity to connected systems is not bi-directional.
        /// </summary>
         public bool SupportsFullImport { get; }

        /// <summary>
        /// Does the Connector support receiving delta imports? i.e. receiving just specific attribute/object changes for objects in the connected system.
        /// It's recommended that a Connector does support this approach where possible as this is the quickest way of receiving changes from connected systems.
        /// </summary>
        public bool SupportsDeltaImport { get; }
        
        /// <summary>
        /// Does the Connector support exporting changes/objects to the connected system? Some systems might be import-only, i.e. source-of-truth/HCM systems.
        /// It's recommended that a Connector does support exports though, to ensure that the system can be updated with attribute values it's not authoritative for, i.e. email-address, phone-numbers, etc in the case of HCM systems.
        /// </summary>
        public bool SupportsExport { get; }

        /// <summary>
        /// Does the Connector support the concept of partitions? Some systems such as LDAP systems do.
        /// </summary>
        public bool SupportsPartitions { get; }

        /// <summary>
        /// Does the Connector support the concept of containers, within partitions? Systems such as LDAP system do.
        /// Note, a Connector must support partitions as well when supporting partition containers.
        /// </summary>
        public bool SupportsPartitionContainers { get; }
    }
}
