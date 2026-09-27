// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Well-known <see cref="ConnectorSetting"/> names that a layer other than the declaring Connector needs to
/// identify by name, rather than by hard-coding the setting's display text where it is consumed.
/// <para>
/// Setting definitions are synchronised into the database as <see cref="ConnectorDefinitionSetting"/> rows, so
/// adding a persisted flag there to mark "this is the setting a particular UI affordance targets" would need a
/// migration for every such affordance. A shared, compiled constant needs none: the Connector that declares the
/// setting (see <c>GetSettings()</c>) and the layer that looks for it by name both reference the same constant,
/// so the two can never drift out of sync, and there is nothing to persist or migrate.
/// </para>
/// </summary>
public static class ConnectorSettingNames
{
    /// <summary>
    /// The LDAP Connector's "Preferred Domain Controller" setting (see LdapConnector). The Connected System
    /// settings page shows a Discover Domain Controllers action beside this field for any Connected System
    /// whose Connector implements <see cref="Interfaces.IConnectorDirectoryServers"/>.
    /// </summary>
    public const string LdapPreferredDomainController = "Preferred Domain Controller";
}
