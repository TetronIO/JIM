// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Connectors.Mock;
using JIM.Connectors.SCIM;
using JIM.Connectors.Sql;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Which Connectors let an administrator say whether an attribute holds several values (#2186). A delimited file's
/// header names columns and nothing more, so only the administrator knows which hold delimited sets; every other
/// Connector reads plurality from its Connected System's schema (the SQL Connector from its related tables), where an
/// override would make JIM misread the data.
/// </summary>
[TestFixture]
public class UserSelectedAttributePluralityCapabilityTests
{
    [Test]
    public void SupportsUserSelectedAttributePlurality_IsDeclaredOnlyByTheFileConnectors()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new FileConnector().SupportsUserSelectedAttributePlurality, Is.True);
            Assert.That(new MockFileConnector().SupportsUserSelectedAttributePlurality, Is.True);
            Assert.That(new MockCallConnector().SupportsUserSelectedAttributePlurality, Is.True);
            Assert.That(new LdapConnector().SupportsUserSelectedAttributePlurality, Is.False);
            Assert.That(new ScimConnector().SupportsUserSelectedAttributePlurality, Is.False);
            Assert.That(new SqlConnector().SupportsUserSelectedAttributePlurality, Is.False);
        }
    }
}
