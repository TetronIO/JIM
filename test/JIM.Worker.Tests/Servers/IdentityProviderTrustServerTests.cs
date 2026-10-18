// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.TestSupport;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Checking JIM's identity provider, and trusting a certificate it presented (#2363). What is trusted is always a
/// certificate the identity provider presents at the moment of the decision, named by the thumbprint the
/// administrator confirmed; nothing is trusted that would not make the identity provider trusted.
/// </summary>
[TestFixture]
public class IdentityProviderTrustServerTests
{
    private const string Authority = "https://adfs.corp.contoso.com/adfs";

    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITrustedCertificateRepository> _mockCertRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IServiceSettingsRepository> _mockSettingsRepo = null!;
    private Mock<IIdentityProviderProbe> _mockProbe = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _administrator = null!;
    private List<TrustedCertificate> _store = null!;

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
        _server.Dispose();
        _issuingCa.Dispose();
        _root.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockCertRepo = new Mock<ITrustedCertificateRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockProbe = new Mock<IIdentityProviderProbe>();
        _store = [];

        _mockRepository.Setup(r => r.TrustedCertificates).Returns(_mockCertRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockSettingsRepo.Object);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockSettingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings { SSOAuthority = Authority });

        _mockCertRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _store.ToList());
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(() => _store.Where(c => c.IsEnabled).ToList());
        _mockCertRepo.Setup(r => r.GetByThumbprintAsync(It.IsAny<string>())).ReturnsAsync((string t) => _store.FirstOrDefault(c => c.Thumbprint == t));
        _mockCertRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) => _store.FirstOrDefault(c => c.Id == id));
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync((string t) => _store.Any(c => c.Thumbprint == t));
        _mockCertRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>())).ReturnsAsync((TrustedCertificate c) => { _store.Add(c); return c; });

        _jim = new JimApplication(_mockRepository.Object) { IdentityProviderTrust = { Probe = _mockProbe.Object } };

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
    }

    #region CheckAsync

    [Test]
    public async Task CheckAsync_NoAuthorityConfigured_NotCheckedAsync()
    {
        _mockSettingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings());

        var status = await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.NotChecked));
        _mockProbe.Verify(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task CheckAsync_ProbesTheAuthoritysConfigurationDocumentAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.TrustedByOperatingSystem);

        await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        _mockProbe.Verify(p => p.ProbeAsync(new Uri("https://adfs.corp.contoso.com/adfs/.well-known/openid-configuration"),
            It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task CheckAsync_PlainHttpAuthority_NotHttpsAsync()
    {
        _mockSettingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings { SSOAuthority = "http://keycloak:8080/realms/jim" });
        _mockProbe.Setup(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityProviderProbeResult { Reached = true, DocumentRead = true });

        var status = await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.NotHttps));
    }

    [Test]
    public async Task CheckAsync_NothingAnswers_UnreachableSayingWhyAsync()
    {
        _mockProbe.Setup(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityProviderProbeResult { Reached = false, Error = "No such host is known." });

        var status = await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.Unreachable));
            Assert.That(status.Message, Does.Contain("No such host is known."));
        }
    }

    [Test]
    public async Task CheckAsync_TrustedByOperatingSystem_TrustedWithTheChainLeafFirstAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.TrustedByOperatingSystem);

        var status = await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.Trusted));
            Assert.That(status.TrustedBy, Is.EqualTo(CertificateStoreTrustOutcome.TrustedByOperatingSystem));
            Assert.That(status.Host, Is.EqualTo("adfs.corp.contoso.com"));
            Assert.That(status.PresentedChain.Select(c => c.Thumbprint), Is.EqualTo(new[] { _server.Thumbprint, _issuingCa.Thumbprint }));
            Assert.That(status.PresentedChain[0].IsServerCertificate, Is.True);
            Assert.That(status.PresentedChain[1].CommonName, Is.EqualTo("Contoso Issuing CA 01"));
        }
    }

    [Test]
    public async Task CheckAsync_Untrusted_NamesTheRootTheServerDidNotSendAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted);

        var status = await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.Untrusted));
            Assert.That(status.UnsentIssuer, Is.EqualTo(_root.Subject));
            Assert.That(status.SuggestedCertificate?.Thumbprint, Is.EqualTo(_issuingCa.Thumbprint));
        }
    }

    [Test]
    public async Task CheckAsync_CertificateInStoreForConnectorsOnly_ShowsItWithItsPurposeAsync()
    {
        var stored = Stored(_issuingCa, CertificateTrustPurpose.Connectors);
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted);

        var status = await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        var issuingCa = status.PresentedChain[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(issuingCa.TrustedCertificateId, Is.EqualTo(stored.Id));
            Assert.That(issuingCa.TrustedFor, Is.EqualTo(CertificateTrustPurpose.Connectors));
            Assert.That(issuingCa.TrustedForSignIn, Is.False);
        }
    }

    [Test]
    public async Task CheckAsync_TrustedByTheStore_NamesTheVouchingCertificateAsync()
    {
        var stored = Stored(_issuingCa, CertificateTrustPurpose.SignIn);
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.TrustedByJimCertificateStore, _issuingCa.Thumbprint);

        var status = await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.Trusted));
            Assert.That(status.VouchingCertificates.Select(c => c.TrustedCertificateId), Is.EqualTo(new Guid?[] { stored.Id }));
            Assert.That(status.SignInDependsOn(stored.Id), Is.True);
        }
    }

    [Test]
    public async Task CheckAsync_PassesOnlyTheSignInCertificatesToTheProbeAsync()
    {
        Stored(_root, CertificateTrustPurpose.Connectors);
        var signIn = Stored(_issuingCa, CertificateTrustPurpose.SignIn);
        List<string>? passed = null;
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.TrustedByOperatingSystem);
        _mockProbe.Setup(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()))
            .Callback((Uri _, IReadOnlyCollection<X509Certificate2> certificates, CancellationToken _) => passed = certificates.Select(c => c.Thumbprint).ToList())
            .ReturnsAsync(Presented(CertificateStoreTrustOutcome.TrustedByOperatingSystem));

        await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        Assert.That(passed, Is.EqualTo(new[] { signIn.Thumbprint }));
    }

    [Test]
    public async Task CheckAsync_NameMismatch_NameMismatchAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.NameMismatch);

        var status = await _jim.IdentityProviderTrust.CheckAsync(CancellationToken.None);

        Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.NameMismatch));
    }

    #endregion

    #region TrustPresentedCertificateAsync

    [Test]
    public async Task TrustPresentedCertificateAsync_IssuingCaThumbprintAsCopied_AddsItTrustedForSignInAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted);
        var asCopied = string.Join(":", _issuingCa.Thumbprint.Chunk(2).Select(p => new string(p))).ToLowerInvariant();

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(asCopied, _administrator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.Trusted));
            Assert.That(result.Certificate?.Thumbprint, Is.EqualTo(_issuingCa.Thumbprint));
            Assert.That(result.Certificate?.TrustedFor, Is.EqualTo(CertificateTrustPurpose.SignIn));
            Assert.That(result.Certificate?.Name, Is.EqualTo("Contoso Issuing CA 01"));
            Assert.That(result.Certificate?.Notes, Does.Contain("adfs.corp.contoso.com"));
        }
        _mockActivityRepo.Verify(r => r.CreateActivityAsync(It.Is<Activity>(a =>
            a.TargetType == ActivityTargetType.TrustedCertificate && a.InitiatedById == _administrator.Id)), Times.Once);
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_NameGiven_UsesItAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted);

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(_issuingCa.Thumbprint, _administrator, name: "AD FS issuing CA");

        Assert.That(result.Certificate?.Name, Is.EqualTo("AD FS issuing CA"));
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_ApiKey_AttributedToTheKeyAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted);
        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "Infrastructure", IsInfrastructureKey = true };

        await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(_issuingCa.Thumbprint, apiKey);

        _mockActivityRepo.Verify(r => r.CreateActivityAsync(It.Is<Activity>(a =>
            a.InitiatedByType == ActivityInitiatorType.ApiKey && a.InitiatedById == apiKey.Id)), Times.Once);
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_ThumbprintNotPresented_TrustsNothingAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted);

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(_root.Thumbprint, _administrator);

        Assert.That(result.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.ThumbprintNotPresented));
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_NoThumbprint_TrustsNothingAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted);

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(" ", _administrator);

        Assert.That(result.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.ThumbprintNotPresented));
        _mockProbe.Verify(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_InStoreForConnectorsOnly_TrustsItForSignInAsWellAsync()
    {
        var stored = Stored(_issuingCa, CertificateTrustPurpose.Connectors);
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted);

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(_issuingCa.Thumbprint, _administrator);

        Assert.That(result.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.TrustedForSignInAsWell));
        _mockCertRepo.Verify(r => r.UpdateAsync(It.Is<TrustedCertificate>(c =>
            c.Id == stored.Id && c.TrustedFor == (CertificateTrustPurpose.Connectors | CertificateTrustPurpose.SignIn))), Times.Once);
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_AlreadyTrustedForSignIn_ChangesNothingAsync()
    {
        Stored(_issuingCa, CertificateTrustPurpose.SignIn);
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.TrustedByJimCertificateStore, _issuingCa.Thumbprint);

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(_issuingCa.Thumbprint, _administrator);

        Assert.That(result.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.AlreadyTrusted));
        _mockCertRepo.Verify(r => r.UpdateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_NameMismatch_TrustsNothingAsync()
    {
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.NameMismatch);

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(_issuingCa.Thumbprint, _administrator);

        Assert.That(result.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.NameMismatch));
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_ExpiredServerCertificate_RefusedAsAnInvalidChainAsync()
    {
        using var expired = TestPki.CreateServer("adfs.corp.contoso.com", _issuingCa, notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome.Untrusted, serverCertificate: expired);

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(_issuingCa.Thumbprint, _administrator);

        Assert.That(result.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.InvalidChain));
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustPresentedCertificateAsync_IdentityProviderUnreachable_TrustsNothingAsync()
    {
        _mockProbe.Setup(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityProviderProbeResult { Reached = false, Error = "Connection refused" });

        var result = await _jim.IdentityProviderTrust.TrustPresentedCertificateAsync(_issuingCa.Thumbprint, _administrator);

        Assert.That(result.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.IdentityProviderUnavailable));
    }

    #endregion

    #region Arrangement

    private void GivenTheIdentityProviderPresents(CertificateStoreTrustOutcome outcome, string? vouching = null, X509Certificate2? serverCertificate = null)
    {
        _mockProbe.Setup(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Presented(outcome, vouching, serverCertificate));
    }

    private IdentityProviderProbeResult Presented(CertificateStoreTrustOutcome outcome, string? vouching = null, X509Certificate2? serverCertificate = null) => new()
    {
        Reached = true,
        DocumentRead = outcome is CertificateStoreTrustOutcome.TrustedByOperatingSystem or CertificateStoreTrustOutcome.TrustedByJimCertificateStore,
        Decision = new CertificateStoreTrustDecision(outcome, vouching == null ? [] : [vouching]),
        ServerCertificate = TestPki.PublicOnly(serverCertificate ?? _server),
        SentByServer = [TestPki.PublicOnly(_issuingCa)]
    };

    private TrustedCertificate Stored(X509Certificate2 certificate, CertificateTrustPurpose trustedFor)
    {
        var stored = new TrustedCertificate
        {
            Id = Guid.NewGuid(),
            Name = certificate.GetNameInfo(X509NameType.SimpleName, false),
            Thumbprint = certificate.Thumbprint,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            SerialNumber = certificate.SerialNumber,
            ValidFrom = certificate.NotBefore.ToUniversalTime(),
            ValidTo = certificate.NotAfter.ToUniversalTime(),
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = certificate.RawData,
            TrustedFor = trustedFor
        };
        _store.Add(stored);
        return stored;
    }

    #endregion
}
