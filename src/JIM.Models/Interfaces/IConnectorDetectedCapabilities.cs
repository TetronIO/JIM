// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to surface human-readable facts it has detected about the target system, so an
/// administrator can see them on the Connected System details page without JIM having to understand any
/// connector-specific data. For example, an LDAP Connector detects the directory type, vendor, and paging
/// support from the target's rootDSE on connection, and persists them for JIM to display here.
/// </summary>
/// <remarks>
/// Named distinctly from <see cref="IConnectorCapabilities"/>, which declares a Connector's static, always-on
/// feature support (import/export/paging and so on): this interface instead reports facts detected at
/// runtime about a specific Connected System instance, and may return a different list per system (or none,
/// before the first successful connection).
/// </remarks>
public interface IConnectorDetectedCapabilities
{
    /// <summary>
    /// Returns the detected capabilities for a Connected System, derived from the Connector's own persisted
    /// connector data. The Connector deserialises its own JSON internally; JIM never parses or understands
    /// connector-specific persisted data itself.
    /// </summary>
    /// <param name="persistedConnectorData">The Connected System's <c>PersistedConnectorData</c>, or null/empty if no connection has completed yet.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <returns>
    /// An ordered list of detected facts for display. Empty when nothing has been detected yet, or when
    /// <paramref name="persistedConnectorData"/> is null, empty, or cannot be parsed; this method must never
    /// throw, since it will typically be called from the UI/API rendering path.
    /// </returns>
    public List<ConnectorCapability> GetDetectedCapabilities(string? persistedConnectorData, ILogger logger);
}
