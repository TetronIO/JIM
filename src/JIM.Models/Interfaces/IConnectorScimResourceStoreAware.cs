// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Interfaces;

/// <summary>
/// A Connector that reads a SCIM store JIM holds, rather than an external system: the SCIM 2.0 Service Provider
/// Connector. JIM hands it the Connected System's store before opening an import connection or reading the schema.
/// </summary>
public interface IConnectorScimResourceStoreAware
{
    /// <summary>
    /// Sets the Connected System's store. A Connector given none must fail rather than read nothing, since an empty
    /// Full Import would mark the whole Connector Space Obsolete.
    /// </summary>
    void SetScimResourceStore(IScimResourceStore? store);
}
