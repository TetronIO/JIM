// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Connectors.Mock;
using JIM.Connectors.SCIM;
using JIM.Connectors.Sql;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Which Connectors declare that they classify a rejection as a value already in use (Unique Value Generation, #242,
/// release 4). Collision Remediation acts only on rejections from a Connector that declares it, so a Connector must
/// declare it exactly when its export path really does classify.
/// </summary>
[TestFixture]
public class UniquenessRejectionClassificationCapabilityTests
{
    [Test]
    public void SupportsUniquenessRejectionClassification_IsDeclaredByTheConnectorsThatClassify()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new LdapConnector().SupportsUniquenessRejectionClassification, Is.True);
            Assert.That(new ScimConnector().SupportsUniquenessRejectionClassification, Is.True);
            Assert.That(new SqlConnector().SupportsUniquenessRejectionClassification, Is.True);
            Assert.That(new FileConnector().SupportsUniquenessRejectionClassification, Is.False);
            Assert.That(new MockCallConnector().SupportsUniquenessRejectionClassification, Is.False);
            Assert.That(new MockFileConnector().SupportsUniquenessRejectionClassification, Is.False);
        }
    }
}
