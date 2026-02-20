using JIM.Models.Staging;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to return a hierarchy of containers present in a Connected System.
/// Containers may or may not belong to a Partition.
/// </summary>
public interface IConnectorContainers
{
    /// <summary>
    /// Gets the top-level container for a connected system, that the Connector is authorised to read.
    /// The container may/is likely to contain child containers to represent a hierarchy of containers.
    /// </summary>
    public ConnectorContainer? GetContainers(IList<ConnectedSystemSettingValue> settings);

    /// <summary>
    /// Gets the top-level container of a partition in a connected system, that the Connector is authorised to read.
    /// The container may/is likely to contain child containers to represent a hierarchy of containers.
    /// </summary>
    /// <remarks> 
    /// If the Connector implements IConnectorPartitions, then it will call this overload for a specific partition, not the basic constructor.
    /// </remarks>
    public ConnectorContainer? GetContainers(IList<ConnectedSystemSettingValue> settings, ConnectorPartition connectorPartition);
}