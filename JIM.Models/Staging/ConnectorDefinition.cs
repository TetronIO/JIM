using JIM.Models.Interfaces;
namespace JIM.Models.Staging;

public class ConnectorDefinition : IConnectorCapabilities
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public string? Url { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Is this a Connector built-in to JIM itself, or third-party supplied?
    /// </summary>
    public bool BuiltIn { get; set; }

    public List<ConnectorDefinitionFile> Files { get; } = new();

    public List<ConnectorDefinitionSetting> Settings { get; set; } = new();

    /// <summary>
    /// Backwards navigation link for EF.
    /// </summary>
    public List<ConnectedSystem>? ConnectedSystems { get; set; }

    #region Capabilities
    /// <summary>
    /// Does the Connector support receiving full imports? i.e. receiving the total representation of all objects in the connected system.
    /// Most should, to enable reconciliation after exports, though some might just be drop-exports, i.e. for when connectivity to connected systems is not bi-directional.
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

    /// <summary>
    /// Some connected systems, such as LDAP-based directories, make use of a secondary identifier when referencing other objects, i.e. a DN, 
    /// even though this is not an immutable identifier, but still has to be used to do things like resolve references. If the connected system needs to use a secondary ID, set this to true.
    /// </summary>
    public bool SupportsSecondaryExternalId { get; set; }

    /// <summary>
    /// Some connectors require the user to specify the attribute to use as the external id as the system is unknown.
    /// </summary>
    public bool SupportsUserSelectedExternalId { get; set; }

    /// <summary>
    /// Some connectors require the user to be able to specify the data type for the attribute, where the system is unknown, or where auto-detection is performed but cannot guarantee accuracy.
    /// </summary>
    public bool SupportsUserSelectedAttributeTypes { get; set; }
    #endregion
}