using JIM.Models.Interfaces;

namespace JIM.Connectors;

/// <summary>
/// Factory for creating connector instances by name.
/// Used by the Worker to resolve the correct connector implementation
/// for a given Connected System's connector definition.
/// </summary>
public interface IConnectorFactory
{
    /// <summary>
    /// Creates a new connector instance for the given connector name.
    /// </summary>
    /// <param name="connectorName">The connector definition name (e.g. "JIM LDAP Connector").</param>
    /// <returns>A new connector instance.</returns>
    /// <exception cref="NotSupportedException">Thrown when the connector name is not recognised.</exception>
    IConnector Create(string connectorName);
}
