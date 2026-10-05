// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Models.Connectors;
using NUnit.Framework;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How JIM assembles a server's certificate chain, and what it concludes from it (#1914).
/// <para>
/// JIM trusts a server when its chain is otherwise valid and any certificate in it is in the JIM certificate store:
/// the root, an intermediate, or the server's own certificate, as the SCIM and LDAP Connectors always have. Trust is
/// judged on what the server sent and what the store holds, never on what JIM downloaded, because that is all a
/// connection in an air-gapped deployment has.
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

        using var chain = Assemble(selfSigned, sent: [], trusted: []);

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
        using var chain = Assemble(_leaf, sent: [_intermediate], trusted: []);

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
        using var chain = Assemble(_leaf, sent: [], trusted: []);

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
        using var chain = Assemble(_leaf, sent: [_intermediate], trusted: [_root]);

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
        using var chain = Assemble(_leaf, sent: [], trusted: [_root]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.False);
            Assert.That(chain.IsTrusted, Is.False);
            Assert.That(chain.MissingIssuer, Is.EqualTo(_intermediate.Subject));
        }
    }

    [Test]
    public void Assemble_IntermediateInTheJimStore_IsTrusted()
    {
        // The defect behind #1914: an intermediate in the store was judged untrusted because it is not a root, while
        // the SCIM and LDAP Connectors accepted it.
        using var chain = Assemble(_leaf, sent: [], trusted: [_intermediate]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsTrusted, Is.True);
            Assert.That(chain.Elements[1].Source, Is.EqualTo(ServerCertificateChainElementSource.JimCertificateStore));
        }
    }

    [Test]
    public void Assemble_ServerCertificateInTheJimStore_IsTrusted()
    {
        using var chain = Assemble(_leaf, sent: [_intermediate], trusted: [_leaf]);

        Assert.That(chain.IsTrusted, Is.True);
    }

    [Test]
    public void Assemble_ACertificateFromAnotherHierarchyInTheJimStore_IsNotTrusted()
    {
        using var otherRoot = TestPki.CreateRoot("Web CA");

        using var chain = Assemble(_leaf, sent: [_intermediate, _root], trusted: [otherRoot]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.True);
            Assert.That(chain.IsTrusted, Is.False);
        }
    }

    [Test]
    public void Assemble_IntermediateNotMarkedAsACertificateAuthority_ReportsAProblem()
    {
        using var notACa = TestPki.CreateIntermediate("Not A CA", _root, isCertificateAuthority: false);
        using var leaf = TestPki.CreateServer("hr-db.corp.local", notACa);

        using var chain = Assemble(leaf, sent: [notACa], trusted: [_root]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsTrusted, Is.False, "A trusted root does not vouch through a certificate that may not issue others.");
            Assert.That(chain.Problem, Does.Contain("Not A CA").And.Contain("certificate authority"));
        }
    }

    [Test]
    public void Assemble_ExpiredIntermediate_ReportsAProblem()
    {
        using var expired = TestPki.CreateIntermediate("Expired CA", _root, notAfter: DateTimeOffset.UtcNow.AddHours(-2));
        using var leaf = TestPki.CreateServer("hr-db.corp.local", expired);

        using var chain = Assemble(leaf, sent: [expired], trusted: [_root]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsTrusted, Is.False, "A trusted root does not vouch through an expired certificate authority.");
            Assert.That(chain.Problem, Does.Contain("Expired CA").And.Contain("expired"));
        }
    }

    [Test]
    public void Assemble_IssuerOnlyAvailableFromTheAddressInTheCertificate_IsDownloadedAndSaysWhereFrom()
    {
        using var server = new CertificateDownloadServer(_intermediate);
        using var leaf = TestPki.CreateServer("hr-db.corp.local", _intermediate, server.Url);

        using var chain = Assemble(leaf, sent: [], trusted: [_root]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.IsComplete, Is.True);
            Assert.That(chain.IsTrusted, Is.False, "A connection that cannot download the intermediate cannot link the server to the root, so the root alone does not yet make this work.");
            Assert.That(chain.Elements[1].Source, Is.EqualTo(ServerCertificateChainElementSource.Downloaded));
            Assert.That(chain.Elements[1].DownloadedFrom, Is.EqualTo(server.Url));
        }
    }

    private static AssembledCertificateChain Assemble(X509Certificate2 leaf, X509Certificate2[] sent, X509Certificate2[] trusted) =>
        CertificateChainAssembler.Assemble(
            TestPki.PublicOnly(leaf),
            sent.Select(TestPki.PublicOnly).ToList(),
            trusted.Select(TestPki.PublicOnly).ToList(),
            Timeout);
}
