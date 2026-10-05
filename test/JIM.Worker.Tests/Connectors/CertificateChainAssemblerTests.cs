// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Models.Connectors;
using NUnit.Framework;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How JIM assembles a server's certificate chain, and what it concludes from it (#1914).
/// <para>
/// JIM trusts a server when its chain ends at a root in the JIM certificate store; the certificate authorities in
/// between must be available but are never trusted themselves. Every case here was first established against .NET's
/// chain builder directly, which is what both JIM's own checks and the SQL Connector's driver rely on: trusting the
/// server's certificate, or an intermediate, never makes a chain trusted.
/// </para>
/// </summary>
[TestFixture]
public class CertificateChainAssemblerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private X509Certificate2 _root = null!;
    private X509Certificate2 _intermediate = null!;
    private X509Certificate2 _leaf = null!;

    [SetUp]
    public void SetUp()
    {
        _root = TestPki.CreateRoot("Corp Root CA");
        _intermediate = TestPki.CreateIntermediate("Corp Issuing CA 2", _root);
        _leaf = TestPki.CreateServer("hr-db.corp.local", _intermediate);
    }

    [TearDown]
    public void TearDown()
    {
        _leaf.Dispose();
        _intermediate.Dispose();
        _root.Dispose();
    }

    [Test]
    public void Assemble_SelfSignedServerCertificate_IsItsOwnCompleteChain()
    {
        using var selfSigned = TestPki.CreateRoot("db.example.com");

        using var chain = Assemble(selfSigned, sent: [], trusted: [], supplied: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.True);
            Assert.That(chain.Root!.Thumbprint, Is.EqualTo(selfSigned.Thumbprint));
            Assert.That(chain.Elements, Has.Count.EqualTo(1));
            Assert.That(chain.IsTrusted, Is.False);
        }
    }

    [Test]
    public void Assemble_ServerSendsItsIntermediateButNotTheRoot_IsIncompleteAndNamesTheRoot()
    {
        using var chain = Assemble(_leaf, sent: [_intermediate], trusted: [], supplied: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.False);
            Assert.That(chain.Root, Is.Null);
            Assert.That(chain.MissingIssuer, Is.EqualTo(_root.Subject), "The root is what the administrator has to ask for.");
            Assert.That(chain.Elements.Select(e => e.Certificate.Thumbprint), Is.EqualTo(new[] { _leaf.Thumbprint, _intermediate.Thumbprint }));
            Assert.That(chain.Elements[1].Source, Is.EqualTo(ServerCertificateChainElementSource.SentByServer));
        }
    }

    [Test]
    public void Assemble_ServerSendsOnlyItsOwnCertificate_NamesTheIssuingCaAsMissing()
    {
        using var chain = Assemble(_leaf, sent: [], trusted: [], supplied: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.False);
            Assert.That(chain.MissingIssuer, Is.EqualTo(_intermediate.Subject));
            Assert.That(chain.Elements, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Assemble_RootInTheJimStoreAndIntermediateSent_IsTrusted()
    {
        using var chain = Assemble(_leaf, sent: [_intermediate], trusted: [_root], supplied: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.True);
            Assert.That(chain.IsTrusted, Is.True);
            Assert.That(chain.Elements[2].Source, Is.EqualTo(ServerCertificateChainElementSource.JimCertificateStore));
        }
    }

    [Test]
    public void Assemble_RootInTheJimStoreButIntermediateUnavailable_IsNotTrusted()
    {
        // The root alone cannot vouch for a certificate it did not issue directly; JIM needs the link between them.
        using var chain = Assemble(_leaf, sent: [], trusted: [_root], supplied: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.False);
            Assert.That(chain.IsTrusted, Is.False);
            Assert.That(chain.MissingIssuer, Is.EqualTo(_intermediate.Subject));
        }
    }

    [Test]
    public void Assemble_IntermediateInTheJimStore_DoesNotMakeTheChainTrusted()
    {
        // The defect behind #1914: an intermediate in the store completes chains but is never a trust anchor.
        using var chain = Assemble(_leaf, sent: [], trusted: [_intermediate], supplied: [TestPki.PublicOnly(_root)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.True);
            Assert.That(chain.IsTrusted, Is.False);
        }
    }

    [Test]
    public void Assemble_ServerCertificateInTheJimStore_DoesNotMakeTheChainTrusted()
    {
        using var chain = Assemble(_leaf, sent: [_intermediate], trusted: [TestPki.PublicOnly(_leaf)], supplied: []);

        Assert.That(chain.IsTrusted, Is.False);
    }

    [Test]
    public void Assemble_SuppliedCertificatesCompleteTheChain_AreMarkedSuppliedAndNotTrusted()
    {
        using var chain = Assemble(_leaf, sent: [], trusted: [], supplied: [TestPki.PublicOnly(_intermediate), TestPki.PublicOnly(_root)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.True);
            Assert.That(chain.Root!.Thumbprint, Is.EqualTo(_root.Thumbprint));
            Assert.That(chain.Elements.Skip(1).Select(e => e.Source), Is.All.EqualTo(ServerCertificateChainElementSource.Supplied));
            Assert.That(chain.IsTrusted, Is.False, "Supplying certificates proposes them; only the JIM certificate store makes a chain trusted.");
            Assert.That(chain.Unrelated, Is.Empty);
            Assert.That(chain.Problem, Is.Null);
        }
    }

    [Test]
    public void Assemble_SuppliedCertificateFromAnotherHierarchy_IsReportedAsUnrelated()
    {
        using var otherRoot = TestPki.CreateRoot("Web CA");

        using var chain = Assemble(_leaf, sent: [_intermediate], trusted: [], supplied: [TestPki.PublicOnly(_root), TestPki.PublicOnly(otherRoot)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.True);
            Assert.That(chain.Unrelated.Select(c => c.Thumbprint), Is.EqualTo(new[] { otherRoot.Thumbprint }));
        }
    }

    [Test]
    public void Assemble_IntermediateNotMarkedAsACertificateAuthority_ReportsAProblem()
    {
        using var notACa = TestPki.CreateIntermediate("Not A CA", _root, isCertificateAuthority: false);
        using var leaf = TestPki.CreateServer("hr-db.corp.local", notACa);

        using var chain = Assemble(leaf, sent: [notACa], trusted: [], supplied: [TestPki.PublicOnly(_root)]);

        Assert.That(chain.Problem, Does.Contain("Not A CA").And.Contain("certificate authority"));
    }

    [Test]
    public void Assemble_ExpiredIntermediate_ReportsAProblem()
    {
        using var expired = TestPki.CreateIntermediate("Expired CA", _root, notAfter: DateTimeOffset.UtcNow.AddHours(-2));
        using var leaf = TestPki.CreateServerAllowingExpiredIssuer("hr-db.corp.local", expired);

        using var chain = Assemble(leaf, sent: [expired], trusted: [], supplied: [TestPki.PublicOnly(_root)]);

        Assert.That(chain.Problem, Does.Contain("Expired CA").And.Contain("expired"));
    }

    [Test]
    public void Assemble_IssuerOnlyAvailableFromTheAddressInTheCertificate_IsDownloadedAndSaysWhereFrom()
    {
        using var server = new CertificateDownloadServer(_intermediate);
        using var leaf = TestPki.CreateServer("hr-db.corp.local", _intermediate, server.Url);

        using var chain = Assemble(leaf, sent: [], trusted: [_root], supplied: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.True);
            Assert.That(chain.IsTrusted, Is.True);
            Assert.That(chain.Elements[1].Source, Is.EqualTo(ServerCertificateChainElementSource.Downloaded));
            Assert.That(chain.Elements[1].DownloadedFrom, Is.EqualTo(server.Url));
        }
    }

    private static AssembledCertificateChain Assemble(X509Certificate2 leaf, X509Certificate2[] sent, X509Certificate2[] trusted, X509Certificate2[] supplied) =>
        CertificateChainAssembler.Assemble(
            TestPki.PublicOnly(leaf),
            sent.Select(TestPki.PublicOnly).ToList(),
            trusted.Select(TestPki.PublicOnly).ToList(),
            supplied,
            Timeout);

    /// <summary>
    /// Serves one certificate over HTTP, standing in for the address a PKI publishes in its certificates'
    /// Authority Information Access extension.
    /// </summary>
    private sealed class CertificateDownloadServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _certificate;
        private readonly Task _serving;

        internal CertificateDownloadServer(X509Certificate2 certificate)
        {
            _certificate = certificate.RawData;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/issuer.cer";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        internal string Url { get; }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    context.Response.ContentType = "application/pkix-cert";
                    await context.Response.OutputStream.WriteAsync(_certificate);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        }

        private static int FreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
