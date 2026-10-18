// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.TestSupport;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A certificate is trusted for Connectors, for sign-in, or both (#2363), and each purpose sees only the certificates
/// trusted for it: a certificate added for a lab SQL Server must never start vouching for the identity provider.
/// </summary>
[TestFixture]
public class CertificateTrustPurposeTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITrustedCertificateRepository> _mockCertRepo = null!;
    private JimApplication _jim = null!;
    private X509Certificate2 _certificate = null!;
    private MetaverseObject _administrator = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockCertRepo = new Mock<ITrustedCertificateRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.TrustedCertificates).Returns(_mockCertRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockCertRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>())).ReturnsAsync((TrustedCertificate c) => c);

        _jim = new JimApplication(_mockRepository.Object);
        _certificate = TestPki.PublicOnly(TestPki.CreateRoot("Contoso Root CA"));

        _administrator = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "User" } };
        _administrator.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Id = 1, Name = Constants.BuiltInAttributes.DisplayName },
            StringValue = "Jo Bloggs"
        });
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
        _certificate.Dispose();
    }

    [Test]
    public async Task AddFromDataAsync_PurposeNotGiven_TrustedForConnectorsOnlyAsync()
    {
        var added = await _jim.Certificates.AddFromDataAsync("Contoso Root CA", _certificate.RawData, _administrator);

        Assert.That(added.TrustedFor, Is.EqualTo(CertificateTrustPurpose.Connectors));
    }

    [Test]
    public async Task AddFromDataAsync_SignInAndConnectors_StoresBothAsync()
    {
        var added = await _jim.Certificates.AddFromDataAsync("Contoso Root CA", _certificate.RawData, _administrator,
            trustedFor: CertificateTrustPurpose.SignIn | CertificateTrustPurpose.Connectors);

        Assert.That(added.TrustedFor, Is.EqualTo(CertificateTrustPurpose.SignIn | CertificateTrustPurpose.Connectors));
    }

    [Test]
    public void AddFromDataAsync_TrustedForNothing_RefusedWithoutStoringAnything()
    {
        Assert.That(async () => await _jim.Certificates.AddFromDataAsync("Contoso Root CA", _certificate.RawData, _administrator, trustedFor: CertificateTrustPurpose.None),
            Throws.TypeOf<ArgumentException>());
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task UpdateAsync_TrustedForChanged_StoresNewPurposeAsync()
    {
        var stored = StoredCertificate(CertificateTrustPurpose.Connectors);
        _mockCertRepo.Setup(r => r.GetByIdAsync(stored.Id)).ReturnsAsync(stored);

        await _jim.Certificates.UpdateAsync(stored.Id, _administrator, trustedFor: CertificateTrustPurpose.Connectors | CertificateTrustPurpose.SignIn);

        _mockCertRepo.Verify(r => r.UpdateAsync(It.Is<TrustedCertificate>(c =>
            c.TrustedFor == (CertificateTrustPurpose.Connectors | CertificateTrustPurpose.SignIn))), Times.Once);
    }

    [Test]
    public void UpdateAsync_TrustedForNothing_RefusedWithoutUpdating()
    {
        var stored = StoredCertificate(CertificateTrustPurpose.Connectors);
        _mockCertRepo.Setup(r => r.GetByIdAsync(stored.Id)).ReturnsAsync(stored);

        Assert.That(async () => await _jim.Certificates.UpdateAsync(stored.Id, _administrator, trustedFor: CertificateTrustPurpose.None),
            Throws.TypeOf<ArgumentException>());
        _mockCertRepo.Verify(r => r.UpdateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task GetTrustedCertificatesAsync_ReturnsOnlyCertificatesTrustedForConnectorsAsync()
    {
        using var signInOnly = TestPki.PublicOnly(TestPki.CreateRoot("Sign-in Only CA"));
        using var both = TestPki.PublicOnly(TestPki.CreateRoot("Both CA"));
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(
        [
            StoredCertificate(CertificateTrustPurpose.Connectors, _certificate),
            StoredCertificate(CertificateTrustPurpose.SignIn, signInOnly),
            StoredCertificate(CertificateTrustPurpose.SignIn | CertificateTrustPurpose.Connectors, both)
        ]);

        var certificates = await _jim.Certificates.GetTrustedCertificatesAsync();

        Assert.That(certificates.Select(c => c.Thumbprint), Is.EquivalentTo(new[] { _certificate.Thumbprint, both.Thumbprint }));
    }

    [Test]
    public async Task GetSignInTrustedCertificatesAsync_ReturnsOnlyCertificatesTrustedForSignInAsync()
    {
        using var signInOnly = TestPki.PublicOnly(TestPki.CreateRoot("Sign-in Only CA"));
        using var both = TestPki.PublicOnly(TestPki.CreateRoot("Both CA"));
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(
        [
            StoredCertificate(CertificateTrustPurpose.Connectors, _certificate),
            StoredCertificate(CertificateTrustPurpose.SignIn, signInOnly),
            StoredCertificate(CertificateTrustPurpose.SignIn | CertificateTrustPurpose.Connectors, both)
        ]);

        var certificates = await _jim.Certificates.GetSignInTrustedCertificatesAsync();

        Assert.That(certificates.Select(c => c.Thumbprint), Is.EquivalentTo(new[] { signInOnly.Thumbprint, both.Thumbprint }));
    }

    private TrustedCertificate StoredCertificate(CertificateTrustPurpose trustedFor, X509Certificate2? certificate = null)
    {
        var x509 = certificate ?? _certificate;
        return new TrustedCertificate
        {
            Id = Guid.NewGuid(),
            Name = x509.Subject,
            Thumbprint = x509.Thumbprint,
            Subject = x509.Subject,
            Issuer = x509.Issuer,
            SerialNumber = x509.SerialNumber,
            ValidFrom = x509.NotBefore.ToUniversalTime(),
            ValidTo = x509.NotAfter.ToUniversalTime(),
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = x509.RawData,
            TrustedFor = trustedFor
        };
    }
}
