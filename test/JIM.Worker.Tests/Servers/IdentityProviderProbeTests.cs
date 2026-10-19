// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.TestSupport;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The probe over a real TLS handshake, against an identity provider whose certificate comes from a private
/// certificate authority (#2363): it must report the chain the server presented even when the handshake is refused,
/// because that chain is what the administrator chooses from.
/// </summary>
[TestFixture]
public class IdentityProviderProbeTests
{
    private X509Certificate2 _root = null!;
    private X509Certificate2 _issuingCa = null!;
    private X509Certificate2 _server = null!;
    private TlsTestServer _identityProvider = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = TestPki.CreateRoot("Contoso Root CA");
        _issuingCa = TestPki.CreateIntermediate("Contoso Issuing CA 01", _root);
        _server = TestPki.CreateServer("localhost", _issuingCa);
        _identityProvider = TlsTestServer.Start(_server, path => path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) ? "{}" : null,
            TestPki.PublicOnly(_issuingCa));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        await _identityProvider.DisposeAsync();
        _server.Dispose();
        _issuingCa.Dispose();
        _root.Dispose();
    }

    [Test]
    public async Task ProbeAsync_CertificateAuthorityNobodyTrusts_ReportsTheRefusedChainAsync()
    {
        var result = await new IdentityProviderProbe().ProbeAsync(Discovery(), [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Reached, Is.True);
            Assert.That(result.DocumentRead, Is.False);
            Assert.That(result.Decision?.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.Untrusted));
            Assert.That(result.ServerCertificate?.Thumbprint, Is.EqualTo(_server.Thumbprint));
            Assert.That(result.SentByServer.Select(c => c.Thumbprint), Does.Contain(_issuingCa.Thumbprint));
        }
    }

    [Test]
    public async Task ProbeAsync_RootTrustedForSignIn_ReadsTheDocumentAsync()
    {
        var result = await new IdentityProviderProbe().ProbeAsync(Discovery(), [TestPki.PublicOnly(_root)], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DocumentRead, Is.True);
            Assert.That(result.Decision?.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.TrustedByJimCertificateStore));
            Assert.That(result.Decision?.VouchingThumbprints, Is.EqualTo(new[] { _root.Thumbprint }));
        }
    }

    [Test]
    public async Task ProbeAsync_HostTheCertificateDoesNotName_NameMismatchAsync()
    {
        var result = await new IdentityProviderProbe().ProbeAsync(Discovery("127.0.0.1"), [TestPki.PublicOnly(_root)], CancellationToken.None);

        Assert.That(result.Decision?.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.NameMismatch));
    }

    [Test]
    public async Task ProbeAsync_NothingListening_NotReachedAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await new IdentityProviderProbe().ProbeAsync(new Uri($"https://localhost:{port}/.well-known/openid-configuration"), [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Reached, Is.False);
            Assert.That(result.Error, Is.Not.Empty);
        }
    }

    private Uri Discovery(string host = "localhost") => _identityProvider.Address(host, "/realms/jim/.well-known/openid-configuration");
}
