// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using JIM.Connectors;
using JIM.Models.Connectors;
using JIM.TestSupport;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Reading an HTTPS server's certificate the way JIM's HTTP Connectors reach the server: through the outbound proxy
/// configured for it. Behind a TLS-inspecting proxy, the certificate that refused a connection is the proxy's, and
/// only a read through the proxy sees it; a direct connection is blocked, or sees the real server's certificate.
/// </summary>
[TestFixture]
public class ServerCertificateProbeProxyTests
{
    // Reserved (RFC 2606), so it never resolves: a read that reaches the server can only have gone through the proxy.
    private const string HostName = "entra.example.test";

    private X509Certificate2 _root = null!;
    private X509Certificate2 _leaf = null!;
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _root = TestPki.CreateRoot("Inspecting Proxy Root CA");
        _leaf = TestPki.CreateServer(HostName, _root);
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        _leaf.Dispose();
        _root.Dispose();
        _logger.Dispose();
    }

    [Test]
    public async Task Read_HttpsThroughAProxy_ReadsTheCertificatePresentedInTheTunnelAsync()
    {
        await using var server = TlsTestServer.Start(_leaf, _ => null);
        await using var proxy = ConnectProxyTestServer.Start(server.Port);

        var reading = Read(SecureHandshakeFraming.Https, new StubProxy(proxy.Address));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading, Is.Not.Null, "the certificate is read through the tunnel");
            Assert.That(reading!.Diagnostic.Thumbprint, Is.EqualTo(_leaf.Thumbprint));
            Assert.That(reading!.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
            Assert.That(proxy.Requests.Select(r => r.Target), Is.EqualTo(new[] { $"{HostName}:443" }));
        }
    }

    [Test]
    public async Task Read_HttpsThroughAProxyThatNeedsCredentials_SendsThemAsBasicAuthenticationAsync()
    {
        await using var server = TlsTestServer.Start(_leaf, _ => null);
        await using var proxy = ConnectProxyTestServer.Start(server.Port);

        var reading = Read(SecureHandshakeFraming.Https, new StubProxy(proxy.Address, new NetworkCredential("jim", "proxy-password")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading, Is.Not.Null);
            Assert.That(proxy.Requests.Single().ProxyAuthorization,
                Is.EqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("jim:proxy-password"))));
        }
    }

    [Test]
    public async Task Read_HttpsProxyRefusesTheTunnel_ReportsTheServerUnreachableAsync()
    {
        // A proxy that wants credentials JIM was not given: a connectivity problem, not a certificate one, so the
        // original failure stands.
        await using var proxy = ConnectProxyTestServer.Refusing("407 Proxy Authentication Required");

        var reading = Read(SecureHandshakeFraming.Https, new StubProxy(proxy.Address));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading, Is.Null);
            Assert.That(proxy.Requests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Read_HttpsToAHostTheProxyBypasses_ConnectsDirectlyAsync()
    {
        using var localLeaf = TestPki.CreateServer("localhost", _root);
        await using var server = TlsTestServer.Start(localLeaf, _ => null);
        await using var proxy = ConnectProxyTestServer.Start(server.Port);

        var reading = ServerCertificateProbe.Read("localhost", server.Port, [], TimeSpan.FromSeconds(10), _logger,
            "Microsoft Entra ID", "HTTPS", SecureHandshakeFraming.Https, proxy: new StubProxy(proxy.Address, bypassed: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading, Is.Not.Null);
            Assert.That(proxy.Requests, Is.Empty);
        }
    }

    [Test]
    public async Task Read_DirectTls_NeverUsesTheHttpProxyAsync()
    {
        // LDAPS and Oracle TCPS do not go through an HTTP proxy, so neither does a read of their certificates.
        await using var server = TlsTestServer.Start(_leaf, _ => null);
        await using var proxy = ConnectProxyTestServer.Start(server.Port);

        var reading = Read(SecureHandshakeFraming.DirectTls, new StubProxy(proxy.Address));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading, Is.Null, "the host does not resolve, and the proxy is not used");
            Assert.That(proxy.Requests, Is.Empty);
        }
    }

    private ServerCertificateReading? Read(SecureHandshakeFraming framing, IWebProxy proxy) =>
        ServerCertificateProbe.Read(HostName, 443, [], TimeSpan.FromSeconds(10), _logger, "Microsoft Entra ID", "HTTPS", framing, proxy: proxy);

    private sealed class StubProxy(Uri address, NetworkCredential? credential = null, bool bypassed = false) : IWebProxy
    {
        public ICredentials? Credentials { get; set; } = credential;

        public Uri GetProxy(Uri destination) => address;

        public bool IsBypassed(Uri host) => bypassed;
    }
}
