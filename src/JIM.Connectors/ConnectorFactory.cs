using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;

namespace JIM.Connectors;

/// <summary>
/// Creates connector instances by name. Handles built-in connectors (LDAP, File).
/// Future user-supplied connectors will extend this factory.
/// </summary>
public class ConnectorFactory : IConnectorFactory
{
    public IConnector Create(string connectorName)
    {
        if (connectorName == ConnectorConstants.LdapConnectorName)
            return new LdapConnector();
        if (connectorName == ConnectorConstants.FileConnectorName)
            return new FileConnector();

        throw new NotSupportedException(
            $"{connectorName} connector not yet supported for worker processing.");
    }
}
