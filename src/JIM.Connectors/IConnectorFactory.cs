// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;

namespace JIM.Connectors;

/// <summary>
/// Factory for creating connector instances by name. This is the single dispatch point for resolving a
/// Connected System's Connector Definition to a connector implementation; used by both the application layer
/// and the Worker, so that connector selection logic exists in exactly one place.
/// </summary>
public interface IConnectorFactory
{
    /// <summary>
    /// Creates a new connector instance for the given connector name. When the connector supports credential
    /// protection (<see cref="IConnectorCredentialAware"/>) or certificate validation (<see cref="IConnectorCertificateAware"/>),
    /// and the corresponding provider is supplied, the factory configures the connector with it before returning.
    /// </summary>
    /// <param name="connectorName">The connector definition name (e.g. "JIM LDAP Connector").</param>
    /// <param name="credentialProtection">The credential protection service to configure on credential-aware connectors, or null to leave it unconfigured.</param>
    /// <param name="certificateProvider">The certificate provider to configure on certificate-aware connectors, or null to leave it unconfigured.</param>
    /// <returns>A new connector instance.</returns>
    /// <exception cref="NotSupportedException">Thrown when the connector name is not recognised.</exception>
    IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null);
}
