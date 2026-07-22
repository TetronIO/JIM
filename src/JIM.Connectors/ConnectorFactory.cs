// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Connectors.SCIM;
using JIM.Models.Interfaces;

namespace JIM.Connectors;

/// <summary>
/// Creates connector instances by name. Handles built-in connectors (LDAP, File) and is the single dispatch
/// point used by both the application layer and the Worker; it also configures credential protection and
/// certificate validation on the created connector when it supports them.
/// Future user-supplied connectors will extend this factory.
/// </summary>
public class ConnectorFactory : IConnectorFactory
{
    public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null)
    {
        var connector = CreateConnectorInstance(connectorName);

        if (connector is IConnectorCredentialAware credentialAware && credentialProtection != null)
            credentialAware.SetCredentialProtection(credentialProtection);

        if (connector is IConnectorCertificateAware certificateAware && certificateProvider != null)
            certificateAware.SetCertificateProvider(certificateProvider);

        return connector;
    }

    /// <summary>
    /// Instantiates the built-in connector matching the given name.
    /// </summary>
    private static IConnector CreateConnectorInstance(string connectorName)
    {
        if (connectorName == ConnectorConstants.LdapConnectorName)
            return new LdapConnector();
        if (connectorName == ConnectorConstants.FileConnectorName)
            return new FileConnector();
        if (connectorName == ConnectorConstants.Scim2ConnectorName)
            return new ScimConnector();

        throw new NotSupportedException(
            $"Connector definition '{connectorName}' is not supported. No built-in connector with that name is registered.");
    }
}
