using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Defines how a connector can provide schema information for a connected system to JIM.
/// </summary>
public interface IConnectorSchema
{
    /// <summary>
    /// Retrieves the schema for a connected system. 
    /// Recommend this is implemented so that the Connector dynamically retrieves the schema from the connected system to reduce re-configuration work in the future if the system changes.
    /// If this isn't viable/desirable, then you can also just hard-code the schema in this method.
    /// </summary>
    public Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settings, ILogger logger);
}