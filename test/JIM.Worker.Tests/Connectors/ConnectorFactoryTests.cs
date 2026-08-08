// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Connectors.SCIM;
using JIM.Connectors.Sql;
using JIM.Models.Interfaces;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class ConnectorFactoryTests
{
    private ConnectorFactory _connectorFactory = null!;

    [SetUp]
    public void SetUp()
    {
        _connectorFactory = new ConnectorFactory();
    }

    [Test]
    public void Create_LdapConnectorName_ReturnsLdapConnector()
    {
        // Act
        using var connector = (LdapConnector)_connectorFactory.Create(ConnectorConstants.LdapConnectorName);

        // Assert
        Assert.That(connector, Is.InstanceOf<LdapConnector>());
    }

    [Test]
    public void Create_FileConnectorName_ReturnsFileConnector()
    {
        // Act
        var connector = _connectorFactory.Create(ConnectorConstants.FileConnectorName);

        // Assert
        Assert.That(connector, Is.InstanceOf<FileConnector>());
    }

    [Test]
    public void Create_UnknownConnectorName_ThrowsNotSupportedExceptionNamingConnector()
    {
        // Arrange
        const string unknownConnectorName = "Nonexistent Connector";

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() => _connectorFactory.Create(unknownConnectorName));
        Assert.That(exception.Message, Does.Contain(unknownConnectorName));
    }

    [Test]
    public void Create_ScimClientConnectorName_ReturnsScimConnector()
    {
        // Act
        var connector = _connectorFactory.Create(ConnectorConstants.ScimClientConnectorName);

        // Assert
        Assert.That(connector, Is.InstanceOf<ScimConnector>());
    }

    [Test]
    public void Create_ScimServiceProviderConnectorName_ThrowsNotSupportedException()
    {
        // Act & Assert: the constant exists in ConnectorConstants but no built-in implementation exists yet (#124).
        Assert.Throws<NotSupportedException>(() => _connectorFactory.Create(ConnectorConstants.ScimServiceProviderConnectorName));
    }

    [Test]
    public void Create_SqlConnectorName_ReturnsSqlConnector()
    {
        // Act
        using var connector = (SqlConnector)_connectorFactory.Create(ConnectorConstants.SqlConnectorName);

        // Assert
        Assert.That(connector, Is.InstanceOf<SqlConnector>());
    }

    [Test]
    public void Create_EveryBuiltInConnectorName_ResolvesToAConnectorOfTheSameType()
    {
        // The two lists that have to agree. SeedingServer.BuiltInConnectors() decides which Connector
        // Definitions administrators can create a Connected System against; ConnectorFactory decides which
        // names the Worker can actually instantiate at run time. A name present in the first and absent
        // from the second is silent at build time and at seeding time, and only surfaces as a
        // NotSupportedException on the administrator's first Run Profile. Nothing else pins the pair
        // together, which is how the JIM SQL Connector could sit in ConnectorConstants, fully implemented,
        // and be reachable from neither.
        var builtIns = SeedingServer.BuiltInConnectors();

        Assert.That(builtIns, Is.Not.Empty);

        using (Assert.EnterMultipleScope())
        {
            foreach (var builtIn in builtIns)
            {
                var created = _connectorFactory.Create(builtIn.Name);
                Assert.That(created, Is.InstanceOf(builtIn.GetType()),
                    $"'{builtIn.Name}' is seeded as a built-in Connector Definition, so the factory must return a {builtIn.GetType().Name} for it.");
                (created as IDisposable)?.Dispose();
                (builtIn as IDisposable)?.Dispose();
            }
        }
    }

    [Test]
    public void Create_WithProviders_AppliesThemWithoutError()
    {
        // Arrange
        var credentialProtection = new Mock<ICredentialProtection>().Object;
        var certificateProvider = new Mock<ICertificateProvider>().Object;

        // Act
        using var connector = (LdapConnector)_connectorFactory.Create(ConnectorConstants.LdapConnectorName, credentialProtection, certificateProvider);

        // Assert: deep observation of the applied providers isn't possible via the public surface; the no-throw
        // and type assertion is sufficient to prove the factory wired them through without error.
        Assert.That(connector, Is.InstanceOf<LdapConnector>());
    }
}
