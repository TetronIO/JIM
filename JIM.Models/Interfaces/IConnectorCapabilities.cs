namespace JIM.Models.Interfaces
{
    /// <summary>
    /// Defines how a Connector can let JIM know what capabilities it supports.
    /// </summary>
    public interface IConnectorCapabilities
    {
        /// <summary>
        /// Does the Connector support receiving full imports? i.e. receiving the total representation of all objects in the connected system.
        /// Most should, to enable reconciliation after exports, though some might just be drop-exports, i.e. for when connectivity to connected systems is not bi-directional.
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

        /// <summary>
        /// Some connected systems, such as LDAP-based directories, make use of a secondary identifier when referencing other objects, i.e. a DN, 
        /// even though this is not an immutable identifier, but still has to be used to do things like resolve references. If the connected system needs to use a secondary ID, set this to true.
        /// </summary>
        public bool SupportsSecondaryExternalId { get; }

        /// <summary>
        /// For some systems it makes sense to allow the user to choose the external id attribute, for others it doesn't.
        /// Use this to control whether or not the user can change the external id. Note, if you set this to false, you will
        /// have to use the RecommendedExternalIdAttribute property on a connector so an external id attribute is set.
        /// </summary>
        public bool SupportsUserSelectedExternalId { get; }

        /// <summary>
        /// Controls whether or not the user can change the data type of a connected system attribute. For systems with a defined
        /// schema, this probably doesn't make sense to allow, but for systems where the schema is inferred, i.e. in CSVs, then it does.
        /// </summary>
        public bool SupportsUserSelectedAttributeTypes {  get; }
    }
}
