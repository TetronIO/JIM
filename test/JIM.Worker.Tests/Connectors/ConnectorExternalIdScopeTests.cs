// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Connectors.Mock;
using JIM.Connectors.SCIM;
using JIM.Connectors.Sql;
using JIM.Models.Interfaces;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Which Connectors vouch that an External ID identifies one object across every Object Type of a Connected System
/// (#2049). Only where that holds can an import tell that an object it already holds under one Object Type is now
/// arriving under another, rather than being a different object that happens to share the value.
/// </summary>
[TestFixture]
public class ConnectorExternalIdScopeTests
{
    [Test]
    public void LdapConnector_ExternalIdsAreUniqueAcrossObjectTypes()
    {
        // objectGUID, entryUUID and nsUniqueId are each unique within a directory, whatever class an entry has.
        IConnectorCapabilities connector = new LdapConnector();

        Assert.That(connector.ExternalIdsUniqueAcrossObjectTypes, Is.True);
    }

    [Test]
    public void SqlConnector_ExternalIdsAreNotUniqueAcrossObjectTypes()
    {
        // A view over a table has the table's keys by construction, and two tables' identity keys overlap (#1285).
        IConnectorCapabilities connector = new SqlConnector();

        Assert.That(connector.ExternalIdsUniqueAcrossObjectTypes, Is.False);
    }

    [Test]
    public void FileConnector_ExternalIdsAreNotUniqueAcrossObjectTypes()
    {
        // Each Object Type's External ID is a column the administrator chose; a person and a department can share 1001.
        IConnectorCapabilities connector = new FileConnector();

        Assert.That(connector.ExternalIdsUniqueAcrossObjectTypes, Is.False);
    }

    [Test]
    public void ScimConnector_ExternalIdsAreNotDeclaredUniqueAcrossObjectTypes()
    {
        // A SCIM resource never changes type, so declaring it would only add risk from a non-conforming provider.
        IConnectorCapabilities connector = new ScimConnector();

        Assert.That(connector.ExternalIdsUniqueAcrossObjectTypes, Is.False);
    }

    [Test]
    public void MockFileConnector_ExternalIdsAreNotUniqueAcrossObjectTypes()
    {
        IConnectorCapabilities connector = new MockFileConnector();

        Assert.That(connector.ExternalIdsUniqueAcrossObjectTypes, Is.False);
    }
}
