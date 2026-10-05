// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Reading the certificates an administrator supplies to complete a server's chain, in the forms a PKI team hands
/// them over: a single certificate, a PEM bundle, or a PKCS#7 (.p7b) file as Windows exports a chain.
/// </summary>
[TestFixture]
public class CertificateBundleReaderTests
{
    private X509Certificate2 _root = null!;
    private X509Certificate2 _intermediate = null!;

    [SetUp]
    public void SetUp()
    {
        _root = TestPki.CreateRoot("Corp Root CA");
        _intermediate = TestPki.CreateIntermediate("Corp Issuing CA 2", _root);
    }

    [TearDown]
    public void TearDown()
    {
        _intermediate.Dispose();
        _root.Dispose();
    }

    [Test]
    public void Read_DerCertificate_ReturnsIt()
    {
        var certificates = CertificateBundleReader.Read(_root.RawData);

        Assert.That(Thumbprints(certificates), Is.EqualTo(new[] { _root.Thumbprint }));
    }

    [Test]
    public void Read_PemBundle_ReturnsEveryCertificate()
    {
        var certificates = CertificateBundleReader.Read(TestPki.ToPemBundle(_intermediate, _root));

        Assert.That(Thumbprints(certificates), Is.EqualTo(new[] { _intermediate.Thumbprint, _root.Thumbprint }));
    }

    [Test]
    public void Read_Pkcs7_ReturnsEveryCertificate()
    {
        var certificates = CertificateBundleReader.Read(TestPki.ToPkcs7(_intermediate, _root));

        Assert.That(Thumbprints(certificates), Is.EquivalentTo(new[] { _intermediate.Thumbprint, _root.Thumbprint }));
    }

    [Test]
    public void Read_PemEncodedPkcs7_ReturnsEveryCertificate()
    {
        var pem = PemEncoding.Write("PKCS7", TestPki.ToPkcs7(_intermediate, _root));

        var certificates = CertificateBundleReader.Read(Encoding.ASCII.GetBytes(pem));

        Assert.That(Thumbprints(certificates), Is.EquivalentTo(new[] { _intermediate.Thumbprint, _root.Thumbprint }));
    }

    [Test]
    public void Read_PemWithAPrivateKeyAlongside_ReturnsOnlyTheCertificates()
    {
        // People export "the certificate" with its key more often than they should. Only public certificate
        // material is ever taken; the key is not read, kept or stored.
        var pem = _root.ExportCertificatePem() + "\n" + PemEncoding.Write("PRIVATE KEY", _root.GetRSAPrivateKey()!.ExportPkcs8PrivateKey());

        var certificates = CertificateBundleReader.Read(Encoding.ASCII.GetBytes(pem));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Thumbprints(certificates), Is.EqualTo(new[] { _root.Thumbprint }));
            Assert.That(certificates[0].HasPrivateKey, Is.False);
        }
    }

    [Test]
    public void Read_SomethingThatIsNotACertificate_ThrowsWithAReasonAnAdministratorCanAct()
    {
        Assert.That(() => CertificateBundleReader.Read(Encoding.ASCII.GetBytes("this is not a certificate")),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains(".cer, .crt, .pem or .p7b"));
    }

    [Test]
    public void Read_Empty_Throws()
    {
        Assert.That(() => CertificateBundleReader.Read([]), Throws.TypeOf<InvalidDataException>());
    }

    private static string[] Thumbprints(IEnumerable<X509Certificate2> certificates) => certificates.Select(c => c.Thumbprint).ToArray();
}
