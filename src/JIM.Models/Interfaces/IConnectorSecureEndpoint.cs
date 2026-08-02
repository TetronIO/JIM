// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Staging;

namespace JIM.Models.Interfaces;

/// <summary>
/// Connectors that make encrypted connections implement this so JIM can work out which server's certificate a
/// Connected System's failure is about, from that system's own settings.
/// </summary>
/// <remarks>
/// This is what keeps the certificate-trust endpoints safe: they take a Connected System, ask its connector where it
/// connects, and probe that. A caller cannot name a host of their own choosing, so the endpoint cannot be turned into
/// a way of making JIM connect to arbitrary addresses.
/// </remarks>
public interface IConnectorSecureEndpoint
{
    /// <summary>
    /// The encrypted endpoint these settings point at.
    /// </summary>
    /// <param name="settingValues">The Connected System's setting values.</param>
    /// <returns>The endpoint, or null where the system is not configured for an encrypted connection, in which case there is no certificate to look at.</returns>
    SecureEndpoint? ResolveSecureEndpoint(List<ConnectedSystemSettingValue> settingValues);
}
