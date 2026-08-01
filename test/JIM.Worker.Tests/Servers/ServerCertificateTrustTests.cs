// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Connectors;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Moq;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers trusting the certificate a Connected System's server presents: the certificate added is the one the server
/// is presenting at the moment of the decision, a certificate that changed since the administrator saw it stops the
/// action, and the endpoint always comes from the Connected System's own settings rather than from a caller.
/// </summary>
[TestFixture]
public class ServerCertificateTrustTests
{
    private const int ConnectedSystemId = 42;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITrustedCertificateRepository> _mockCertRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IServerCertificateReader> _mockReader = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _testUser = null!;

    private PresentedServerCertificate _leaf = null!;
    private PresentedServerCertificate _issuer = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockCertRepo = new Mock<ITrustedCertificateRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockReader = new Mock<IServerCertificateReader>();

        _mockRepository.Setup(r => r.TrustedCertificates).Returns(_mockCertRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(new List<TrustedCertificate>());
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mockCertRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>())).ReturnsAsync((TrustedCertificate c) => c);

        _jim = new JimApplication(_mockRepository.Object)
        {
            Certificates =
            {
                ServerCertificateReader = _mockReader.Object,
                ConnectorFactory = new StubConnectorFactory()
            }
        };

        _testUser = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        _testUser.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Id = 1, Name = Constants.BuiltInAttributes.DisplayName },
            StringValue = "Test User"
        });

        GenerateChain();
        GivenTheConnectedSystemIs(StubConnector.SecureConnectorName);
        GivenTheServerPresents(_leaf, _issuer);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    #region Arrangement

    /// <summary>
    /// Builds a certificate authority and a certificate it issued, so leaf and issuer are genuinely different
    /// certificates with different thumbprints, as they are on a real server.
    /// </summary>
    private void GenerateChain()
    {
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest("CN=Corp Issuing CA 2, O=Corp", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(5));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=hr.corp.local, O=Corp", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var server = serverRequest.Create(authority, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), Guid.NewGuid().ToByteArray());

        _leaf = Describe(server);
        _issuer = Describe(authority);
    }

    private static PresentedServerCertificate Describe(X509Certificate2 certificate) => new()
    {
        Thumbprint = certificate.Thumbprint,
        Subject = certificate.Subject,
        Issuer = certificate.Issuer,
        ValidFrom = certificate.NotBefore.ToUniversalTime(),
        ValidTo = certificate.NotAfter.ToUniversalTime(),
        Data = certificate.Export(X509ContentType.Cert)
    };

    private void GivenTheConnectedSystemIs(string connectorName, string? baseUrl = "https://hr.corp.local/scim/v2")
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(ConnectedSystemWith(baseUrl, connectorName));
    }

    private static ConnectedSystem ConnectedSystemWith(string? baseUrl, string connectorName = StubConnector.SecureConnectorName)
    {
        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR Cloud",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = connectorName },
            SettingValues =
            [
                new ConnectedSystemSettingValue
                {
                    Setting = new ConnectorDefinitionSetting { Id = 1, Name = StubConnector.SettingBaseUrl },
                    StringValue = baseUrl
                }
            ]
        };
    }

    /// <summary>
    /// The Base URL as the administrator has just typed it, not as it was last saved.
    /// </summary>
    private static List<ConnectedSystemSettingValueDraft> Drafts(string baseUrl) =>
        [new ConnectedSystemSettingValueDraft { SettingId = 1, StringValue = baseUrl }];

    private static ServerCertificateReading Reading(PresentedServerCertificate leaf, PresentedServerCertificate? issuer)
    {
        return new ServerCertificateReading
        {
            Diagnostic = new ServerCertificateDiagnostic
            {
                Host = "hr.corp.local",
                Port = 443,
                Subject = leaf.Subject,
                Issuer = leaf.Issuer,
                Thumbprint = leaf.Thumbprint,
                IssuerThumbprint = issuer?.Thumbprint,
                FailureReason = ServerCertificateFailureReason.UntrustedIssuer
            },
            Chain = new PresentedServerCertificateChain
            {
                Host = "hr.corp.local",
                Port = 443,
                ReadAt = DateTime.UtcNow,
                Leaf = leaf,
                Issuer = issuer
            }
        };
    }

    private void GivenTheServerPresents(PresentedServerCertificate? leaf, PresentedServerCertificate? issuer)
    {
        if (leaf == null)
        {
            _mockReader.Setup(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()))
                .Returns((ServerCertificateReading?)null);
            return;
        }

        _mockReader.Setup(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()))
            .Returns(Reading(leaf, issuer));
    }

    #endregion

    #region Trusting

    [Test]
    public async Task TrustServerCertificateAsync_WithTheThumbprintTheAdministratorConfirmed_AddsThatCertificateAsync()
    {
        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, _leaf.Thumbprint, _testUser);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.Trusted));
            Assert.That(result.Certificate, Is.Not.Null);
            Assert.That(result.Certificate!.Thumbprint, Is.EqualTo(_leaf.Thumbprint));
            Assert.That(result.Certificate!.Name, Is.EqualTo("hr.corp.local"));
        });
    }

    [Test]
    public async Task TrustServerCertificateAsync_WithTheIssuersThumbprint_AddsTheIssuerRatherThanTheLeafAsync()
    {
        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, _issuer.Thumbprint, _testUser);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.Trusted));
            Assert.That(result.Certificate!.Thumbprint, Is.EqualTo(_issuer.Thumbprint));
            Assert.That(result.Certificate!.Name, Is.EqualTo("Corp Issuing CA 2"));
        });
    }

    [Test]
    public async Task TrustServerCertificateAsync_WithASpacedThumbprint_StillMatchesTheCertificateAsync()
    {
        var spaced = string.Join(' ', Enumerable.Range(0, _leaf.Thumbprint.Length / 2).Select(i => _leaf.Thumbprint.Substring(i * 2, 2)));

        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, spaced, _testUser);

        Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.Trusted));
    }

    [Test]
    public async Task TrustServerCertificateAsync_WhenTheServerIsPresentingADifferentCertificate_TrustsNothingAsync()
    {
        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, "0123456789ABCDEF0123456789ABCDEF01234567", _testUser);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.ThumbprintMismatch));
            Assert.That(result.PresentedThumbprint, Is.EqualTo(_leaf.Thumbprint));
            Assert.That(result.Message, Does.Contain("nothing has been trusted"));
        });
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustServerCertificateAsync_WithNoThumbprint_TrustsNothingAsync()
    {
        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, "   ", _testUser);

        Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.ThumbprintMismatch));
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustServerCertificateAsync_WhenTheServerCannotBeReached_TrustsNothingAsync()
    {
        GivenTheServerPresents(null, null);

        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, _leaf.Thumbprint, _testUser);

        Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.ServerUnreachable));
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustServerCertificateAsync_WhenTheCertificateIsAlreadyTrusted_SaysSoRatherThanFailingAsync()
    {
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(_leaf.Thumbprint)).ReturnsAsync(true);

        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, _leaf.Thumbprint, _testUser);

        Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.AlreadyTrusted));
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustServerCertificateAsync_WhenTheSystemMakesNoEncryptedConnection_OffersNothingToTrustAsync()
    {
        GivenTheConnectedSystemIs(StubConnector.PlainConnectorName);

        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, _leaf.Thumbprint, _testUser);

        Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.NotConfiguredForSecureConnection));
        _mockReader.Verify(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()), Times.Never);
    }

    [Test]
    public async Task TrustServerCertificateAsync_WhenTheSystemIsNotConfiguredForTls_OffersNothingToTrustAsync()
    {
        GivenTheConnectedSystemIs(StubConnector.SecureConnectorName, baseUrl: null);

        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, _leaf.Thumbprint, _testUser);

        Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.NotConfiguredForSecureConnection));
        _mockReader.Verify(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()), Times.Never);
    }

    [Test]
    public async Task TrustServerCertificateAsync_WithAnUnknownConnectedSystem_ReportsItWasNotFoundAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, _leaf.Thumbprint, _testUser);

        Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.ConnectedSystemNotFound));
    }

    /// <summary>
    /// The endpoint is the whole security property of this feature: it comes from the Connected System's own
    /// settings, so no caller can make JIM connect to an address of their choosing.
    /// </summary>
    [Test]
    public async Task TrustServerCertificateAsync_ReadsTheEndpointTheConnectedSystemIsConfiguredForAsync()
    {
        SecureEndpoint? probed = null;
        _mockReader
            .Setup(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()))
            .Callback((SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> _) => probed = endpoint)
            .Returns((ServerCertificateReading?)null);

        await _jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, _leaf.Thumbprint, _testUser);

        Assert.That(probed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(probed!.Host, Is.EqualTo("hr.corp.local"));
            Assert.That(probed!.Port, Is.EqualTo(443));
        });
    }

    #endregion

    #region Settings that have not been saved

    /// <summary>
    /// The case the whole action exists for. JIM does not save settings that fail validation, and a certificate it
    /// does not trust is a validation failure, so an administrator configuring a new Connected System has the address
    /// on screen and nothing in the database. Without the drafts, JIM would look at the wrong server.
    /// </summary>
    [Test]
    public async Task ReadServerCertificateAsync_WithSettingsThatHaveNotBeenSaved_LooksAtTheEndpointOnScreenAsync()
    {
        GivenTheConnectedSystemIs(StubConnector.SecureConnectorName, baseUrl: "https://old.corp.local/scim/v2");
        SecureEndpoint? probed = null;
        _mockReader
            .Setup(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()))
            .Callback((SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> _) => probed = endpoint)
            .Returns((ServerCertificateReading?)null);

        await _jim.Certificates.ReadServerCertificateAsync(ConnectedSystemId, Drafts("https://hr.corp.local:8443/scim/v2"));

        Assert.That(probed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(probed!.Host, Is.EqualTo("hr.corp.local"));
            Assert.That(probed!.Port, Is.EqualTo(8443));
        });
    }

    [Test]
    public async Task TrustServerCertificateAsync_WithSettingsThatHaveNotBeenSaved_TrustsTheCertificateFromThatEndpointAsync()
    {
        GivenTheConnectedSystemIs(StubConnector.SecureConnectorName, baseUrl: "https://old.corp.local/scim/v2");
        SecureEndpoint? probed = null;
        _mockReader
            .Setup(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()))
            .Callback((SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> _) => probed = endpoint)
            .Returns(Reading(_leaf, null));

        var result = await _jim.Certificates.TrustServerCertificateAsync(
            ConnectedSystemId, _leaf.Thumbprint, _testUser, changeReason: null, draftSettingValues: Drafts("https://hr.corp.local/scim/v2"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.Trusted));
            Assert.That(probed!.Host, Is.EqualTo("hr.corp.local"));
        });
    }

    /// <summary>
    /// A draft that does not describe an encrypted connection is not silently ignored in favour of a saved one that
    /// does; the administrator is told there is nothing to look at, which is what their screen says.
    /// </summary>
    [Test]
    public async Task ReadServerCertificateAsync_WhenTheUnsavedSettingsAreNotEncrypted_OffersNothingToLookAtAsync()
    {
        var result = await _jim.Certificates.ReadServerCertificateAsync(ConnectedSystemId, Drafts("http://hr.corp.local/scim/v2"));

        Assert.That(result.Outcome, Is.EqualTo(ServerCertificateReadOutcome.NotConfiguredForSecureConnection));
    }

    /// <summary>
    /// Nothing about where a system connects is a secret, and a certificate lookup has no business holding a
    /// credential, so a draft for an encrypted setting is discarded rather than applied.
    /// </summary>
    [Test]
    public async Task ReadServerCertificateAsync_WithADraftForAnEncryptedSetting_LeavesTheSavedValueAloneAsync()
    {
        var connectedSystem = ConnectedSystemWith("https://hr.corp.local/scim/v2");
        var secret = new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Id = 2, Name = "Password", Type = ConnectedSystemSettingType.StringEncrypted },
            StringValue = "saved"
        };
        connectedSystem.SettingValues.Add(secret);
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);

        await _jim.Certificates.ReadServerCertificateAsync(ConnectedSystemId,
            [new ConnectedSystemSettingValueDraft { SettingId = 2, StringValue = "supplied by the caller" }]);

        Assert.That(secret.StringValue, Is.EqualTo("saved"));
    }

    #endregion

    #region Reading

    [Test]
    public async Task ReadServerCertificateAsync_ReturnsWhatTheServerIsPresentingAsync()
    {
        var result = await _jim.Certificates.ReadServerCertificateAsync(ConnectedSystemId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ServerCertificateReadOutcome.Read));
            Assert.That(result.Diagnostic, Is.Not.Null);
            Assert.That(result.Diagnostic!.Thumbprint, Is.EqualTo(_leaf.Thumbprint));
            Assert.That(result.Diagnostic!.IsIssuerCertificateAvailable, Is.True);
            Assert.That(result.ReadAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task ReadServerCertificateAsync_StoresNothingAsync()
    {
        await _jim.Certificates.ReadServerCertificateAsync(ConnectedSystemId);

        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task ReadServerCertificateAsync_WhenTheServerCannotBeReached_SaysSoRatherThanBlamingTheCertificateAsync()
    {
        GivenTheServerPresents(null, null);

        var result = await _jim.Certificates.ReadServerCertificateAsync(ConnectedSystemId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ServerCertificateReadOutcome.ServerUnreachable));
            Assert.That(result.Diagnostic, Is.Null);
        });
    }

    #endregion

    #region Stubs

    /// <summary>
    /// Stands in for the connector factory so the tests exercise the endpoint-resolution path without depending on
    /// a real connector's settings vocabulary.
    /// </summary>
    private class StubConnectorFactory : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null)
        {
            return connectorName == StubConnector.PlainConnectorName
                ? new StubPlainConnector()
                : new StubConnector();
        }
    }

    private class StubConnector : IConnector, IConnectorSecureEndpoint
    {
        public const string SecureConnectorName = "Stub Secure Connector";
        public const string PlainConnectorName = "Stub Plain Connector";
        public const string SettingBaseUrl = "Base URL";

        public string Name => SecureConnectorName;

        public string? Description => null;

        public string? Url => null;

        public SecureEndpoint? ResolveSecureEndpoint(List<ConnectedSystemSettingValue> settingValues)
        {
            var baseUrl = settingValues.SingleOrDefault(s => s.Setting.Name == SettingBaseUrl)?.StringValue;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return null;

            return new SecureEndpoint(uri.Host, uri.IsDefaultPort ? 443 : uri.Port, TimeSpan.FromSeconds(10), "test server", "HTTPS");
        }
    }

    /// <summary>
    /// A connector that never makes an encrypted connection, and so does not implement
    /// <see cref="IConnectorSecureEndpoint"/> at all.
    /// </summary>
    private class StubPlainConnector : IConnector
    {
        public string Name => StubConnector.PlainConnectorName;

        public string? Description => null;

        public string? Url => null;
    }

    #endregion
}
