// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using JIM.Models.Core;
using JIM.TestSupport;

namespace JIM.Utilities.Tests;

/// <summary>
/// The rule a TLS validation callback applies: the operating system's verdict first, then the JIM certificate store,
/// which can only answer for an unknown certificate authority and never for a name mismatch.
/// </summary>
[TestFixture]
public class CertificateStoreTrustTests
{
    private X509Certificate2 _root = null!;
    private X509Certificate2 _issuingCa = null!;
    private X509Certificate2 _server = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = TestPki.CreateRoot("Contoso Root CA");
        _issuingCa = TestPki.CreateIntermediate("Contoso Issuing CA 01", _root);
        _server = TestPki.CreateServer("adfs.corp.contoso.com", _issuingCa);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _root.Dispose();
        _issuingCa.Dispose();
        _server.Dispose();
    }

    [Test]
    public void Evaluate_OperatingSystemTrustsChain_TrustedByOperatingSystemWithoutConsultingStore()
    {
        using var chain = ChainSentByServer();

        var decision = CertificateStoreTrust.Evaluate(Public(_server), chain, SslPolicyErrors.None, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.TrustedByOperatingSystem));
            Assert.That(decision.IsTrusted, Is.True);
            Assert.That(decision.VouchingThumbprints, Is.Empty);
        }
    }

    [Test]
    public void Evaluate_RootInStore_TrustedByStoreNamingTheRoot()
    {
        using var chain = ChainSentByServer();

        var decision = CertificateStoreTrust.Evaluate(Public(_server), chain, SslPolicyErrors.RemoteCertificateChainErrors, [Public(_root)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.TrustedByJimCertificateStore));
            Assert.That(decision.VouchingThumbprints, Is.EqualTo(new[] { _root.Thumbprint }));
        }
    }

    [Test]
    public void Evaluate_IssuingCaInStoreAndRootNeverSent_TrustedByStoreNamingTheIssuingCa()
    {
        using var chain = ChainSentByServer();

        var decision = CertificateStoreTrust.Evaluate(Public(_server), chain, SslPolicyErrors.RemoteCertificateChainErrors, [Public(_issuingCa)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.TrustedByJimCertificateStore));
            Assert.That(decision.VouchingThumbprints, Is.EqualTo(new[] { _issuingCa.Thumbprint }));
        }
    }

    [Test]
    public void Evaluate_IssuingCaAndRootInStore_NamesBothLeafFirst()
    {
        using var chain = ChainSentByServer();

        var decision = CertificateStoreTrust.Evaluate(Public(_server), chain, SslPolicyErrors.RemoteCertificateChainErrors, [Public(_root), Public(_issuingCa)]);

        Assert.That(decision.VouchingThumbprints, Is.EqualTo(new[] { _issuingCa.Thumbprint, _root.Thumbprint }));
    }

    [Test]
    public void Evaluate_SelfSignedServerCertificateInStore_TrustedByStore()
    {
        using var selfSigned = TestPki.CreateRoot("keycloak.corp.contoso.com");

        var decision = CertificateStoreTrust.Evaluate(Public(selfSigned), null, SslPolicyErrors.RemoteCertificateChainErrors, [Public(selfSigned)]);

        Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.TrustedByJimCertificateStore));
    }

    [Test]
    public void Evaluate_NameMismatchEvenThoughStoreVouches_NameMismatch()
    {
        using var chain = ChainSentByServer();

        var decision = CertificateStoreTrust.Evaluate(Public(_server), chain,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, [Public(_root)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.NameMismatch));
            Assert.That(decision.IsTrusted, Is.False);
        }
    }

    [Test]
    public void Evaluate_NoCertificatePresented_NoCertificate()
    {
        var decision = CertificateStoreTrust.Evaluate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable, [Public(_root)]);

        Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.NoCertificate));
    }

    [Test]
    public void Evaluate_ExpiredServerCertificateUnderTrustedRoot_Untrusted()
    {
        using var expired = TestPki.CreateServer("adfs.corp.contoso.com", _issuingCa, notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        using var chain = ChainSentByServer();

        var decision = CertificateStoreTrust.Evaluate(Public(expired), chain, SslPolicyErrors.RemoteCertificateChainErrors, [Public(_root)]);

        Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.Untrusted));
    }

    [Test]
    public void Evaluate_EmptyStore_Untrusted()
    {
        using var chain = ChainSentByServer();

        var decision = CertificateStoreTrust.Evaluate(Public(_server), chain, SslPolicyErrors.RemoteCertificateChainErrors, []);

        Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.Untrusted));
    }

    [Test]
    public void Evaluate_StoreHoldsUnrelatedCertificateAuthority_Untrusted()
    {
        using var otherRoot = TestPki.CreateRoot("Fabrikam Root CA");
        using var chain = ChainSentByServer();

        var decision = CertificateStoreTrust.Evaluate(Public(_server), chain, SslPolicyErrors.RemoteCertificateChainErrors, [Public(otherRoot)]);

        Assert.That(decision.Outcome, Is.EqualTo(CertificateStoreTrustOutcome.Untrusted));
    }

    [Test]
    public void VouchesFor_IssuingCaSentByServerAndRootInStore_True()
    {
        Assert.That(CertificateStoreTrust.VouchesFor(Public(_server), [Public(_issuingCa)], [Public(_root)]), Is.True);
    }

    [Test]
    public void VouchesFor_EmptyStore_False()
    {
        Assert.That(CertificateStoreTrust.VouchesFor(Public(_server), [Public(_issuingCa)], []), Is.False);
    }

    /// <summary>
    /// A chain as SslStream hands it to a validation callback: the intermediates the server sent sit in the extra store.
    /// </summary>
    private X509Chain ChainSentByServer()
    {
        var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.Add(Public(_issuingCa));
        return chain;
    }

    private static X509Certificate2 Public(X509Certificate2 certificate) => TestPki.PublicOnly(certificate);
}
