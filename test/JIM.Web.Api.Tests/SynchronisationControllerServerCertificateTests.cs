// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Connectors;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Covers the two server-certificate endpoints on SynchronisationController: reading what a Connected System's
/// server presents, and trusting it. Both take a Connected System rather than a host and port, and reading stores
/// nothing.
/// </summary>
[TestFixture]
public class SynchronisationControllerServerCertificateTests
{
    private const int ConnectedSystemId = 42;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ITrustedCertificateRepository> _mockCertRepo = null!;
    private Mock<IServerCertificateReader> _mockReader = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private PresentedServerCertificate _leaf = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockCertRepo = new Mock<ITrustedCertificateRepository>();
        _mockReader = new Mock<IServerCertificateReader>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.TrustedCertificates).Returns(_mockCertRepo.Object);

        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(new List<TrustedCertificate>());
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mockCertRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>())).ReturnsAsync((TrustedCertificate c) => c);

        _application = new JimApplication(_mockRepository.Object);
        _application.Certificates.ServerCertificateReader = _mockReader.Object;
        _application.Certificates.ConnectorFactory = new StubConnectorFactory();

        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        // API-key authentication context, so the audit Activity has an initiator, as it does in production.
        var apiKeyId = Guid.NewGuid();
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new ApiKey { Id = apiKeyId, Name = "TestApiKey" });

        var identity = new ClaimsIdentity(
        [
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        ], "ApiKey");

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        GenerateCertificate();
        GivenTheConnectedSystemExists();
        GivenTheServerPresentsItsCertificate();
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    #region Reading

    [Test]
    public async Task GetServerCertificateAsync_ReturnsWhatTheServerIsPresentingAsync()
    {
        var result = await _controller.GetServerCertificateAsync(ConnectedSystemId) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var response = result!.Value as ServerCertificateResponse;
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.Certificate.Thumbprint, Is.EqualTo(_leaf.Thumbprint));
            Assert.That(response!.ReadAt, Is.Not.EqualTo(default(DateTime)));
        }
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task GetServerCertificateAsync_WithAnUnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.GetServerCertificateAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetServerCertificateAsync_WhenTheServerCannotBeReached_ReturnsBadGatewayAsync()
    {
        _mockReader
            .Setup(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()))
            .Returns((ServerCertificateReading?)null);

        var result = await _controller.GetServerCertificateAsync(ConnectedSystemId) as ObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.StatusCode, Is.EqualTo(StatusCodes.Status502BadGateway));
    }

    [Test]
    public async Task GetServerCertificateAsync_WhenTheSystemMakesNoEncryptedConnection_ReturnsBadRequestAsync()
    {
        GivenTheConnectedSystemExists(StubConnector.PlainConnectorName);

        var result = await _controller.GetServerCertificateAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    /// <summary>
    /// The endpoint an administrator is configuring is not in the database, because JIM does not save settings that
    /// fail validation and a certificate it does not trust is a validation failure. The POST carries what is on
    /// screen so the certificate they are shown is the one blocking them.
    /// </summary>
    [Test]
    public async Task ReadServerCertificateAsync_WithSettingsThatHaveNotBeenSaved_ReadsTheEndpointOnScreenAsync()
    {
        GivenTheConnectedSystemExists(baseUrl: "https://old.corp.local/scim/v2");
        SecureEndpoint? probed = null;
        _mockReader
            .Setup(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()))
            .Callback((SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> _) => probed = endpoint)
            .Returns((ServerCertificateReading?)null);

        await _controller.ReadServerCertificateAsync(ConnectedSystemId, new ReadServerCertificateRequest
        {
            SettingValues = new Dictionary<int, ConnectedSystemSettingValueUpdate>
            {
                { 1, new ConnectedSystemSettingValueUpdate { StringValue = "https://hr.corp.local:8443/scim/v2" } }
            }
        });

        Assert.That(probed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probed!.Host, Is.EqualTo("hr.corp.local"));
            Assert.That(probed!.Port, Is.EqualTo(8443));
        }
    }

    #endregion

    #region Trusting

    [Test]
    public async Task TrustServerCertificateAsync_WithTheThumbprintTheAdministratorConfirmed_ReturnsCreatedAsync()
    {
        var result = await _controller.TrustServerCertificateAsync(ConnectedSystemId, new TrustServerCertificateRequest { Thumbprint = _leaf.Thumbprint }) as ObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.StatusCode, Is.EqualTo(StatusCodes.Status201Created));
        var response = result!.Value as TrustServerCertificateResponse;
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.Trusted));
            Assert.That(response!.Certificate, Is.Not.Null);
        }
    }

    [Test]
    public async Task TrustServerCertificateAsync_WhenTheServerIsPresentingSomethingElse_ReturnsConflictWithBothThumbprintsAsync()
    {
        var result = await _controller.TrustServerCertificateAsync(ConnectedSystemId, new TrustServerCertificateRequest { Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567" }) as ConflictObjectResult;

        Assert.That(result, Is.Not.Null);
        var response = result!.Value as TrustServerCertificateResponse;
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.ThumbprintMismatch));
            Assert.That(response!.ExpectedThumbprint, Is.EqualTo("0123456789ABCDEF0123456789ABCDEF01234567"));
            Assert.That(response!.PresentedThumbprint, Is.EqualTo(_leaf.Thumbprint));
        }
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task TrustServerCertificateAsync_WithoutAThumbprint_ReturnsBadRequestAsync()
    {
        var result = await _controller.TrustServerCertificateAsync(ConnectedSystemId, new TrustServerCertificateRequest { Thumbprint = "  " });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockReader.Verify(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()), Times.Never);
    }

    [Test]
    public async Task TrustServerCertificateAsync_WithAnUnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.TrustServerCertificateAsync(ConnectedSystemId, new TrustServerCertificateRequest { Thumbprint = _leaf.Thumbprint });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task TrustServerCertificateAsync_WhenTheCertificateIsAlreadyTrusted_ReturnsOkAsync()
    {
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(_leaf.Thumbprint)).ReturnsAsync(true);

        var result = await _controller.TrustServerCertificateAsync(ConnectedSystemId, new TrustServerCertificateRequest { Thumbprint = _leaf.Thumbprint }) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var response = result!.Value as TrustServerCertificateResponse;
        Assert.That(response!.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.AlreadyTrusted));
    }

    #endregion

    #region Arrangement

    private void GenerateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=hr.corp.local, O=Corp", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        _leaf = new PresentedServerCertificate
        {
            Thumbprint = certificate.Thumbprint,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            ValidFrom = certificate.NotBefore.ToUniversalTime(),
            ValidTo = certificate.NotAfter.ToUniversalTime(),
            Data = certificate.Export(X509ContentType.Cert)
        };
    }

    private void GivenTheConnectedSystemExists(string connectorName = StubConnector.SecureConnectorName, string baseUrl = "https://hr.corp.local/scim/v2")
    {
        var connectedSystem = new ConnectedSystem
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

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);
    }

    private void GivenTheServerPresentsItsCertificate()
    {
        _mockReader
            .Setup(r => r.Read(It.IsAny<SecureEndpoint>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>()))
            .Returns(new ServerCertificateReading
            {
                Diagnostic = new ServerCertificateDiagnostic
                {
                    Host = "hr.corp.local",
                    Port = 443,
                    Subject = _leaf.Subject,
                    Issuer = _leaf.Issuer,
                    Thumbprint = _leaf.Thumbprint,
                    IsSelfSigned = true,
                    FailureReason = ServerCertificateFailureReason.UntrustedIssuer
                },
                Chain = new PresentedServerCertificateChain
                {
                    Host = "hr.corp.local",
                    Port = 443,
                    ReadAt = DateTime.UtcNow,
                    IsSelfSigned = true,
                    Leaf = _leaf
                }
            });
    }

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
