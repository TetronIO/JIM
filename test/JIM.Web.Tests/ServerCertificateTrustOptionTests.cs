// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What an administrator is offered to trust from a server's certificate chain (#1914): any certificate in it, the
/// most durable first.
/// </summary>
[TestFixture]
public class ServerCertificateTrustOptionTests
{
    private static ServerCertificateChainElement Element(string commonName, string issuerCommonName, string thumbprint,
        ServerCertificateChainElementSource source = ServerCertificateChainElementSource.SentByServer, string? downloadedFrom = null)
    {
        return new ServerCertificateChainElement
        {
            Subject = $"CN={commonName}",
            Issuer = $"CN={issuerCommonName}",
            Thumbprint = thumbprint,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidTo = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc),
            IsCertificateAuthority = commonName != "hr-db.corp.local",
            IsSelfSigned = commonName == issuerCommonName,
            Source = source,
            DownloadedFrom = downloadedFrom
        };
    }

    private static ServerCertificateDiagnostic Diagnostic(params ServerCertificateChainElement[] chain)
    {
        var leaf = chain[0];
        return new ServerCertificateDiagnostic
        {
            Host = "hr-db.corp.local",
            Port = 1433,
            Subject = leaf.Subject,
            Issuer = leaf.Issuer,
            Thumbprint = leaf.Thumbprint,
            ValidFrom = leaf.ValidFrom,
            ValidTo = leaf.ValidTo,
            IsSelfSigned = leaf.IsSelfSigned,
            FailureReason = ServerCertificateFailureReason.UntrustedIssuer,
            Chain = [.. chain],
            IsChainComplete = chain[^1].IsSelfSigned,
            RootThumbprint = chain[^1].IsSelfSigned ? chain[^1].Thumbprint : null
        };
    }

    private static readonly ServerCertificateChainElement Server = Element("hr-db.corp.local", "Corp Issuing CA 2", "AA");

    private static readonly ServerCertificateChainElement IssuingCa = Element("Corp Issuing CA 2", "Corp Root CA", "BB",
        ServerCertificateChainElementSource.Downloaded, "http://pki.corp.local/aia/CorpIssuingCA2.crt");

    private static readonly ServerCertificateChainElement Root = Element("Corp Root CA", "Corp Root CA", "CC",
        ServerCertificateChainElementSource.Downloaded, "http://pki.corp.local/aia/CorpRootCA.crt");

    [Test]
    public void For_CompleteChain_OffersEveryCertificateMostDurableFirstWithTheRootRecommended()
    {
        var options = ServerCertificateTrustOption.For(Diagnostic(Server, IssuingCa, Root));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Select(o => o.Thumbprint), Is.EqualTo(new[] { "CC", "BB", "AA" }));
            Assert.That(options.Select(o => o.Role), Is.EqualTo(new[] { ServerCertificateChainRole.Root, ServerCertificateChainRole.Intermediate, ServerCertificateChainRole.Server }));
            Assert.That(options.Select(o => o.IsRecommended), Is.EqualTo(new[] { true, false, false }));
        }
    }

    [Test]
    public void For_IncompleteChain_RecommendsTheHighestCertificateJimFound()
    {
        var options = ServerCertificateTrustOption.For(Diagnostic(Server, IssuingCa));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Select(o => o.Thumbprint), Is.EqualTo(new[] { "BB", "AA" }));
            Assert.That(options[0].Role, Is.EqualTo(ServerCertificateChainRole.Intermediate));
            Assert.That(options[0].IsRecommended, Is.True);
        }
    }

    [Test]
    public void For_OnlyTheServersCertificate_OffersItAloneWithNothingToRecommendOver()
    {
        var options = ServerCertificateTrustOption.For(Diagnostic(Server));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Select(o => o.Thumbprint), Is.EqualTo(new[] { "AA" }));
            Assert.That(options[0].IsRecommended, Is.False);
        }
    }

    [Test]
    public void For_SelfSignedServerCertificate_OffersItAsItsOwnRoot()
    {
        var selfSigned = Element("db01", "db01", "DD");

        var options = ServerCertificateTrustOption.For(Diagnostic(selfSigned));

        Assert.That(options.Single().Role, Is.EqualTo(ServerCertificateChainRole.Root));
    }

    [Test]
    public void For_DownloadedCertificate_NamesTheCertificateThatGaveTheAddress()
    {
        var options = ServerCertificateTrustOption.For(Diagnostic(Server, IssuingCa, Root));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options[0].AddressGivenBy, Is.EqualTo("Corp Issuing CA 2"));
            Assert.That(options[1].AddressGivenBy, Is.EqualTo("hr-db.corp.local"));
        }
    }

    [Test]
    public void For_CertificateAboveAnIntermediateJimDownloaded_StoresThatIntermediateToo()
    {
        // A connection cannot always download the intermediate itself, so trusting what is above it alone would not
        // link back to the server's certificate.
        var options = ServerCertificateTrustOption.For(Diagnostic(Server, IssuingCa, Root));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options[0].AlsoStored.Select(e => e.Thumbprint), Is.EqualTo(new[] { "BB" }));
            Assert.That(options[1].AlsoStored, Is.Empty);
            Assert.That(options[2].AlsoStored, Is.Empty);
        }
    }

    [Test]
    public void For_CertificateAboveAnIntermediateTheServerSends_StoresNothingElse()
    {
        var sentCa = Element("Corp Issuing CA 2", "Corp Root CA", "BB");

        var options = ServerCertificateTrustOption.For(Diagnostic(Server, sentCa, Root));

        Assert.That(options[0].AlsoStored, Is.Empty);
    }

    [Test]
    public void For_DiagnosticRecordedBeforeJimDescribedChains_OffersTheServerCertificateAndTheIssuerItNamed()
    {
        var diagnostic = Diagnostic(Server);
        diagnostic.Chain = [];
        diagnostic.IssuerThumbprint = "BB";

        var options = ServerCertificateTrustOption.For(diagnostic);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Select(o => o.Thumbprint), Is.EqualTo(new[] { "BB", "AA" }));
            Assert.That(options[0].CommonName, Is.EqualTo("Corp Issuing CA 2"));
        }
    }
}
