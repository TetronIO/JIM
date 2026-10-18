// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using JIM.TestSupport;
using JIM.Web.Authentication;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Trusting the identity provider's certificate authority has to take effect on the next sign-in, with no restart
/// (#2363). The authentication handlers fetch the OpenID Connect configuration through IdentityModel's configuration
/// manager over <see cref="IdentityProviderBackchannel"/>; these pin that, while it holds no configuration, the
/// manager tries again on every call rather than backing off, so nothing JIM-side is needed to make a newly trusted
/// certificate count. An IdentityModel upgrade that starts backing off there fails this fixture.
/// </summary>
[TestFixture]
public class IdentityProviderConfigurationRecoveryTests
{
    private X509Certificate2 _root = null!;
    private X509Certificate2 _server = null!;
    private TlsTestServer _identityProvider = null!;
    private List<X509Certificate2> _signInCertificates = null!;
    private HttpClient _backchannel = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = TestPki.CreateRoot("Contoso Root CA");
        _server = TestPki.CreateServer("localhost", _root);
        _identityProvider = TlsTestServer.Start(_server, path => path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal)
            ? $$"""{"issuer":"https://localhost/realms/jim","authorization_endpoint":"https://localhost/realms/jim/auth","token_endpoint":"https://localhost/realms/jim/token"}"""
            : null);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        await _identityProvider.DisposeAsync();
        _server.Dispose();
        _root.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _signInCertificates = [];
        var snapshot = new SignInCertificateSnapshot(_ => Task.FromResult<IReadOnlyCollection<X509Certificate2>>(_signInCertificates.ToArray()),
            TimeProvider.System, freshFor: TimeSpan.Zero);
        _backchannel = new HttpClient(new IdentityProviderBackchannel(snapshot).CreateHandler());
    }

    [TearDown]
    public void TearDown()
    {
        _backchannel.Dispose();
    }

    [Test]
    public async Task GetConfigurationAsync_TrustedIdentityProvider_ReadsItsEndpointsAsync()
    {
        _signInCertificates.Add(TestPki.PublicOnly(_root));
        var manager = NewManager();

        var configuration = await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.That(configuration.AuthorizationEndpoint, Is.EqualTo("https://localhost/realms/jim/auth"));
    }

    [Test]
    public async Task GetConfigurationAsync_CertificateTrustedAfterRepeatedRefusals_SucceedsOnTheVeryNextCallAsync()
    {
        var manager = NewManager();
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.That(async () => await manager.GetConfigurationAsync(CancellationToken.None), Throws.InstanceOf<InvalidOperationException>());

        _signInCertificates.Add(TestPki.PublicOnly(_root));
        var configuration = await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.That(configuration.TokenEndpoint, Is.EqualTo("https://localhost/realms/jim/token"));
    }

    private ConfigurationManager<OpenIdConnectConfiguration> NewManager() =>
        new($"https://localhost:{_identityProvider.Port}/realms/jim/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(_backchannel));
}
