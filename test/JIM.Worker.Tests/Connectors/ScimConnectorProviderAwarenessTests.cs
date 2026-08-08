// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Connectors.SCIM;
using JIM.Models.Interfaces;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The connector accepts the credential protection and certificate providers that
/// <see cref="ConnectorFactory"/> supplies, now that the HTTP client needs both: secrets must be
/// decrypted, and JIM's trusted certificates must reach TLS validation.
/// </summary>
[TestFixture]
public class ScimConnectorProviderAwarenessTests
{
    [Test]
    public void ScimConnector_DeclaresCredentialAndCertificateAwareness()
    {
        var connector = new ScimConnector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector, Is.InstanceOf<IConnectorCredentialAware>());
            Assert.That(connector, Is.InstanceOf<IConnectorCertificateAware>());
        }
    }

    [Test]
    public void Create_WithProviders_WiresThemIntoTheScimConnector()
    {
        var factory = new ConnectorFactory();
        var credentialProtection = new Mock<ICredentialProtection>().Object;
        var certificateProvider = new Mock<ICertificateProvider>().Object;

        var connector = factory.Create(ConnectorConstants.ScimClientConnectorName, credentialProtection, certificateProvider);

        // The providers are held privately, so the observable contract is that dispatch applies them
        // without error and still returns the SCIM connector.
        Assert.That(connector, Is.InstanceOf<ScimConnector>());
    }

    [Test]
    public void SetProviders_CalledWithNull_IsTolerated()
    {
        // The factory passes null when a deployment has neither configured, and the connector must not
        // fail at construction; the failure, if any, belongs at the point of use.
        var connector = new ScimConnector();

        using (Assert.EnterMultipleScope())
        {
            Assert.DoesNotThrow(() => connector.SetCredentialProtection(null));
            Assert.DoesNotThrow(() => connector.SetCertificateProvider(null));
        }
    }
}
