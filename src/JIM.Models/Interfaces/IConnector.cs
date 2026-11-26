// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Interfaces;

public interface IConnector
{
    /// <summary>
    /// A stable identifier for the Connector, of the form <c>publisher.connector</c> in lower case (for example
    /// <c>jim.ldap</c>). It names the Connector's message catalogue (<c>connector:jim.ldap/Messages</c>), so records the
    /// Connector wrote read in an administrator's language, and it must never change once the Connector has shipped,
    /// even when <see cref="Name"/> does.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// The name of the Connector. This will be shown when an administrator wants to configure a new instance of the Connector.
    /// The administrator will be able to provide their own name for an instance of the Connector.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The default description for the Connector. This will be shown when an administrator wants to configure a new instance of the Connector.
    /// The administrator will be able to provide their own description for an instance of the Connector to make it relevant to their environment.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// The URL where more information on the Connector can be found. This will be shown when an administrator wants to configure a new instance of this Connector.
    /// </summary>
    public string? Url { get; }
}