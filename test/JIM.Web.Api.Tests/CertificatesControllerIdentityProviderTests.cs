// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Security;
using JIM.TestSupport;
using JIM.Web.Authentication;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for trusting a certificate the identity provider presents, and for choosing what a certificate is
/// trusted for (#2363). Trusting from the identity provider is an Administrator-only action; the infrastructure API key
/// holds the Administrator role, which is how the first certificate is trusted before anyone can sign in.
/// </summary>
[TestFixture]
public class CertificatesControllerIdentityProviderTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITrustedCertificateRepository> _mockCertRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<IServiceSettingsRepository> _mockSettingsRepo = null!;
    private Mock<IIdentityProviderProbe> _mockProbe = null!;
    private Mock<IIdentityProviderTrustMonitor> _mockMonitor = null!;
    private JimApplication _application = null!;
    private CertificatesController _controller = null!;
    private List<TrustedCertificate> _store = null!;
    private X509Certificate2 _root = null!;
    private X509Certificate2 _server = null!;

    [SetUp]
    public void SetUp()
    {
        _root = TestPki.CreateRoot("Contoso Root CA");
        _server = TestPki.CreateServer("adfs.corp.contoso.com", _root);
        _store = [];

        _mockRepository = new Mock<IRepository>();
        _mockCertRepo = new Mock<ITrustedCertificateRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockProbe = new Mock<IIdentityProviderProbe>();
        _mockMonitor = new Mock<IIdentityProviderTrustMonitor>();

        _mockRepository.Setup(r => r.TrustedCertificates).Returns(_mockCertRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockSettingsRepo.Object);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockSettingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings { SSOAuthority = "https://adfs.corp.contoso.com/adfs" });
        _mockCertRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _store.ToList());
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(() => _store.ToList());
        _mockCertRepo.Setup(r => r.GetByThumbprintAsync(It.IsAny<string>())).ReturnsAsync((string t) => _store.FirstOrDefault(c => c.Thumbprint == t));
        _mockCertRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) => _store.FirstOrDefault(c => c.Id == id));
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync((string t) => _store.Any(c => c.Thumbprint == t));
        _mockCertRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>())).ReturnsAsync((TrustedCertificate c) => { _store.Add(c); return c; });

        _mockProbe.Setup(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new IdentityProviderProbeResult
            {
                Reached = true,
                Decision = new CertificateStoreTrustDecision(CertificateStoreTrustOutcome.Untrusted, []),
                ServerCertificate = TestPki.PublicOnly(_server),
                SentByServer = []
            });
        _mockMonitor.Setup(m => m.Current).Returns(new IdentityProviderTrustStatus { State = IdentityProviderTrustState.NotChecked });
        _mockMonitor.Setup(m => m.RecheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityProviderTrustStatus { State = IdentityProviderTrustState.Trusted, Host = "adfs.corp.contoso.com" });

        _application = new JimApplication(_mockRepository.Object) { IdentityProviderTrust = { Probe = _mockProbe.Object } };
        _controller = new CertificatesController(new Mock<ILogger<CertificatesController>>().Object, _application, _mockMonitor.Object);

        var apiKeyId = Guid.NewGuid();
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new ApiKey { Id = apiKeyId, Name = "Infrastructure", IsInfrastructureKey = true });
        var identity = new ClaimsIdentity(
        [
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "Infrastructure")
        ], "ApiKey");
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
        _server.Dispose();
        _root.Dispose();
    }

    [Test]
    public void TrustIdentityProviderCertificateAsync_IsAdministratorOnly()
    {
        var method = typeof(CertificatesController).GetMethod(nameof(CertificatesController.TrustIdentityProviderCertificateAsync))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(typeof(CertificatesController).GetCustomAttribute<AuthorizeAttribute>()?.Roles, Is.EqualTo("Administrator"));
            Assert.That(method.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Null);
        }
    }

    [Test]
    public async Task TrustIdentityProviderCertificateAsync_PresentedCertificate_Returns201WithItTrustedForSignInAsync()
    {
        var result = await _controller.TrustIdentityProviderCertificateAsync(new TrustIdentityProviderCertificateRequest { Thumbprint = _server.Thumbprint }, CancellationToken.None) as ObjectResult;

        var response = (TrustIdentityProviderCertificateResponse)result!.Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCodes.Status201Created));
            Assert.That(response.Outcome, Is.EqualTo(IdentityProviderCertificateTrustOutcome.Trusted));
            Assert.That(response.Certificate?.TrustedFor, Is.EqualTo(CertificateTrustPurpose.SignIn));
            Assert.That(response.Status?.State, Is.EqualTo(IdentityProviderTrustState.Trusted), "the status reported is the one after trusting");
        }
        _mockMonitor.Verify(m => m.RecheckAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TrustIdentityProviderCertificateAsync_NoThumbprint_Returns400Async()
    {
        var result = await _controller.TrustIdentityProviderCertificateAsync(new TrustIdentityProviderCertificateRequest { Thumbprint = "" }, CancellationToken.None) as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task TrustIdentityProviderCertificateAsync_ThumbprintNotPresented_Returns409Async()
    {
        var result = await _controller.TrustIdentityProviderCertificateAsync(new TrustIdentityProviderCertificateRequest { Thumbprint = _root.Thumbprint }, CancellationToken.None) as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
    }

    [Test]
    public async Task TrustIdentityProviderCertificateAsync_IdentityProviderUnreachable_Returns502Async()
    {
        _mockProbe.Setup(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyCollection<X509Certificate2>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityProviderProbeResult { Reached = false, Error = "Connection refused" });

        var result = await _controller.TrustIdentityProviderCertificateAsync(new TrustIdentityProviderCertificateRequest { Thumbprint = _server.Thumbprint }, CancellationToken.None) as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status502BadGateway));
    }

    [Test]
    public async Task AddFromDataAsync_TrustedForSignIn_StoresItTrustedForSignInAsync()
    {
        var result = await _controller.AddFromDataAsync(new AddCertificateFromDataRequest
        {
            Name = "Contoso Root CA",
            CertificateDataBase64 = Convert.ToBase64String(_root.RawData),
            TrustedFor = CertificateTrustPurpose.SignIn
        }) as ObjectResult;

        Assert.That(((TrustedCertificateDetailDto)result!.Value!).TrustedFor, Is.EqualTo(CertificateTrustPurpose.SignIn));
    }

    [Test]
    public async Task AddFromDataAsync_TrustedForNothing_Returns400Async()
    {
        var result = await _controller.AddFromDataAsync(new AddCertificateFromDataRequest
        {
            Name = "Contoso Root CA",
            CertificateDataBase64 = Convert.ToBase64String(_root.RawData),
            TrustedFor = CertificateTrustPurpose.None
        }) as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Never);
    }

    [Test]
    public async Task UpdateAsync_TrustedFor_StoresTheNewPurposeAsync()
    {
        var stored = Stored(CertificateTrustPurpose.Connectors);

        await _controller.UpdateAsync(stored.Id, new UpdateCertificateRequest { TrustedFor = CertificateTrustPurpose.Connectors | CertificateTrustPurpose.SignIn });

        _mockCertRepo.Verify(r => r.UpdateAsync(It.Is<TrustedCertificate>(c => c.TrustedFor == (CertificateTrustPurpose.Connectors | CertificateTrustPurpose.SignIn))), Times.Once);
    }

    [Test]
    public async Task UpdateAsync_TrustedForNothing_Returns400Async()
    {
        var stored = Stored(CertificateTrustPurpose.Connectors);

        var result = await _controller.UpdateAsync(stored.Id, new UpdateCertificateRequest { TrustedFor = CertificateTrustPurpose.None }) as ObjectResult;

        Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task GetByIdAsync_CertificateSignInDependsOn_SaysSoAsync()
    {
        var stored = Stored(CertificateTrustPurpose.SignIn);
        _mockMonitor.Setup(m => m.Current).Returns(new IdentityProviderTrustStatus
        {
            State = IdentityProviderTrustState.Trusted,
            VouchingCertificates = [new IdentityProviderCertificate { TrustedCertificateId = stored.Id, Thumbprint = stored.Thumbprint }]
        });

        var result = await _controller.GetByIdAsync(stored.Id) as OkObjectResult;

        var detail = (TrustedCertificateDetailDto)result!.Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail.TrustedFor, Is.EqualTo(CertificateTrustPurpose.SignIn));
            Assert.That(detail.UsedBySignIn, Is.True);
            Assert.That(detail.SignInDependsOnIt, Is.True);
        }
    }

    [Test]
    public async Task GetAllAsync_HeadersCarryPurposeAndSignInUseAsync()
    {
        var stored = Stored(CertificateTrustPurpose.SignIn);
        _mockMonitor.Setup(m => m.Current).Returns(new IdentityProviderTrustStatus
        {
            State = IdentityProviderTrustState.Trusted,
            VouchingCertificates = [new IdentityProviderCertificate { TrustedCertificateId = stored.Id, Thumbprint = stored.Thumbprint }]
        });

        var result = await _controller.GetAllAsync(new PaginationRequest()) as OkObjectResult;

        var header = ((PaginatedResponse<TrustedCertificateHeader>)result!.Value!).Items.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.TrustedFor, Is.EqualTo(CertificateTrustPurpose.SignIn));
            Assert.That(header.UsedBySignIn, Is.True);
        }
    }

    private TrustedCertificate Stored(CertificateTrustPurpose trustedFor)
    {
        var stored = new TrustedCertificate
        {
            Id = Guid.NewGuid(),
            Name = "Contoso Root CA",
            Thumbprint = _root.Thumbprint,
            Subject = _root.Subject,
            Issuer = _root.Issuer,
            SerialNumber = _root.SerialNumber,
            ValidFrom = _root.NotBefore.ToUniversalTime(),
            ValidTo = _root.NotAfter.ToUniversalTime(),
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = _root.RawData,
            TrustedFor = trustedFor
        };
        _store.Add(stored);
        return stored;
    }
}
