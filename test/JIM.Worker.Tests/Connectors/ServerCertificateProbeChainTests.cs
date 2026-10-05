// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Models.Connectors;
using NUnit.Framework;
using Serilog;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What the certificate probe reports about a server's certificate chain (#1914), against an in-process TLS server
/// that sends exactly the certificates each test needs.
/// </summary>
/// <remarks>
/// The probe's verdict drives what an administrator is offered to trust, and for the SQL Connector whether a
/// certificate is handed to the driver at all. Each case pins what JIM concludes from what a server sent and what JIM
/// holds.
/// </remarks>
[TestFixture]
public class ServerCertificateProbeChainTests
{
    private const string HostName = "localhost";

    private X509Certificate2 _root = null!;
    private X509Certificate2 _intermediate = null!;
    private X509Certificate2 _leaf = null!;
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _root = TestPki.CreateRoot("Corp Root CA");
        _intermediate = TestPki.CreateIntermediate("Corp Issuing CA 2", _root);
        _leaf = TestPki.CreateServer(HostName, _intermediate);
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        _leaf.Dispose();
        _intermediate.Dispose();
        _root.Dispose();
        _logger.Dispose();
    }

    [Test]
    public void Read_ServerSendsItsIntermediateButTheRootIsUnavailable_ReportsAnIncompleteChainNamingTheRoot()
    {
        using var server = new TlsServer(_leaf, _intermediate);

        var reading = Read(server, trusted: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
            Assert.That(reading.Diagnostic.IsChainComplete, Is.False);
            Assert.That(reading.Diagnostic.MissingIssuer, Is.EqualTo(_root.Subject));
            Assert.That(reading.Diagnostic.RootThumbprint, Is.Null);
            Assert.That(reading.Diagnostic.Remediation, Does.Contain("Corp Root CA"), "The administrator is told what to ask for.");
            Assert.That(reading.Diagnostic.Chain.Select(e => e.Source), Is.EqualTo(new[] { ServerCertificateChainElementSource.SentByServer, ServerCertificateChainElementSource.SentByServer }));
            Assert.That(reading.Chain!.Root, Is.Null);
            Assert.That(reading.Chain!.Intermediates.Select(c => c.Thumbprint), Is.EqualTo(new[] { _intermediate.Thumbprint }));
            Assert.That(reading.Chain!.MissingIssuer, Is.EqualTo(_root.Subject));
        }
    }

    [Test]
    public void Read_RootInTheJimStoreAndTheServerSendsItsIntermediate_FindsNothingWrong()
    {
        // Before #1914 the probe judged trust from the JIM store alone, ignoring the intermediate the server had just
        // sent, so a correctly trusted two-tier chain was still reported as untrusted.
        using var server = new TlsServer(_leaf, _intermediate);

        var reading = Read(server, trusted: [TestPki.PublicOnly(_root)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.None));
            Assert.That(reading.Diagnostic.IsChainComplete, Is.True);
            Assert.That(reading.Diagnostic.Chain[2].Source, Is.EqualTo(ServerCertificateChainElementSource.JimCertificateStore));
        }
    }

    [Test]
    public void Read_IntermediateInTheJimStore_FindsNothingWrong()
    {
        // #1914: the probe demanded a root in the store, so an intermediate an administrator had added was still
        // reported as untrusted, while the SCIM and LDAP Connectors accepted it.
        using var server = new TlsServer(_leaf, _intermediate, _root);

        var reading = Read(server, trusted: [TestPki.PublicOnly(_intermediate)]);

        Assert.That(reading.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.None));
    }

    [Test]
    public void Read_ServerCertificateInTheJimStore_FindsNothingWrong()
    {
        using var server = new TlsServer(_leaf, _intermediate);

        var reading = Read(server, trusted: [TestPki.PublicOnly(_leaf)]);

        Assert.That(reading.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.None));
    }

    [Test]
    public void Read_ExpiredIntermediateUnderATrustedRoot_ReportsTheBrokenChainRatherThanOfferingTrust()
    {
        using var expired = TestPki.CreateIntermediate("Expired CA", _root, notAfter: DateTimeOffset.UtcNow.AddHours(-2));
        using var leaf = TestPki.CreateServer(HostName, expired);
        using var server = new TlsServer(leaf, expired, _root);

        var reading = Read(server, trusted: [TestPki.PublicOnly(_root)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.InvalidChain));
            Assert.That(reading.Diagnostic.Remediation, Does.Contain("Expired CA"));
        }
    }

    [Test]
    public void Read_RootDownloadableFromTheAddressTheIntermediateNames_OffersTheRootAndSaysWhereFrom()
    {
        // A server that sends its root is covered against a real directory (ServerCertificateProbeTests); .NET's own
        // TLS server will not send one, so here the root comes from the address the intermediate names for it.
        using var download = new CertificateDownloadServer(_root);
        using var intermediate = TestPki.CreateIntermediate("Corp Issuing CA 3", _root, issuerDownloadUrl: download.Url);
        using var leaf = TestPki.CreateServer(HostName, intermediate);
        using var server = new TlsServer(leaf, intermediate);

        var reading = Read(server, trusted: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
            Assert.That(reading.Diagnostic.IsChainComplete, Is.True);
            Assert.That(reading.Diagnostic.RootThumbprint, Is.EqualTo(_root.Thumbprint));
            Assert.That(reading.Diagnostic.RootSubject, Is.EqualTo(_root.Subject));
            Assert.That(reading.Diagnostic.IssuerThumbprint, Is.EqualTo(intermediate.Thumbprint));
            Assert.That(reading.Diagnostic.Chain[2].DownloadedFrom, Is.EqualTo(download.Url));
            Assert.That(reading.Chain!.Root!.Thumbprint, Is.EqualTo(_root.Thumbprint));
            Assert.That(reading.Chain!.Root!.Source, Is.EqualTo(ServerCertificateChainElementSource.Downloaded));
            Assert.That(reading.Chain!.Intermediates.Single().Source, Is.EqualTo(ServerCertificateChainElementSource.SentByServer));
            Assert.That(reading.Diagnostic.Remediation, Does.Contain("Corp Root CA"));
        }
    }

    [Test]
    public void Read_ServerSendsOnlyItsOwnCertificate_StillOffersItAndNamesWhatIsMissing()
    {
        using var server = new TlsServer(_leaf);

        var reading = Read(server, trusted: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
            Assert.That(reading.Diagnostic.IsChainComplete, Is.False);
            Assert.That(reading.Diagnostic.IssuerThumbprint, Is.Null);
            Assert.That(reading.Diagnostic.MissingIssuer, Is.EqualTo(_intermediate.Subject));
            Assert.That(reading.Diagnostic.Remediation, Does.Contain("Corp Issuing CA 2"));
            Assert.That(reading.Chain!.All.Select(c => c.Thumbprint), Is.EqualTo(new[] { _leaf.Thumbprint }));
        }
    }

    [Test]
    public void Read_SelfSignedServerCertificate_IsItsOwnRoot()
    {
        using var selfSigned = TestPki.CreateRoot(HostName);
        using var server = new TlsServer(selfSigned);

        var reading = Read(server, trusted: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.Diagnostic.IsSelfSigned, Is.True);
            Assert.That(reading.Diagnostic.IsChainComplete, Is.True);
            Assert.That(reading.Diagnostic.RootThumbprint, Is.EqualTo(selfSigned.Thumbprint));
            Assert.That(reading.Chain!.Root!.Thumbprint, Is.EqualTo(selfSigned.Thumbprint));
            Assert.That(reading.Chain!.Intermediates, Is.Empty);
        }
    }

    private ServerCertificateReading Read(TlsServer server, X509Certificate2[] trusted)
    {
        var reading = ServerCertificateProbe.Read(HostName, server.Port, trusted, TimeSpan.FromSeconds(10), _logger,
            "database server", "TLS", SecureHandshakeFraming.DirectTls);

        Assert.That(reading, Is.Not.Null, "The server answered, so the probe must have seen its certificate.");
        return reading!;
    }

    /// <summary>
    /// Serves TLS with the given server certificate and the further certificates it sends alongside, once.
    /// </summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly SslStreamCertificateContext _context;
        private readonly Task _serving;

        internal TlsServer(X509Certificate2 certificate, params X509Certificate2[] alsoSend)
        {
            _context = SslStreamCertificateContext.Create(certificate, new X509Certificate2Collection(alsoSend.Select(TestPki.PublicOnly).ToArray()), offline: true);
            _listener.Start();
            _serving = Task.Run(ServeOneAsync);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task ServeOneAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = _context });
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // The probe refuses every certificate, so the handshake never completes.
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _serving.Wait(TimeSpan.FromSeconds(10));
        }
    }
}
