// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="CertificatesController.DownloadAsync"/>. The not-found cases run the result through the
/// action's own result filters and MVC's real result executor, because the defect they cover (a 406 Not Acceptable in
/// place of a 404) arises from content negotiation, which calling the action method alone never reaches.
/// </summary>
[TestFixture]
public class CertificateDownloadApiTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<ITrustedCertificateRepository> _certificateRepo = null!;
    private JimApplication _application = null!;
    private CertificatesController _controller = null!;
    private string? _tempFile;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _certificateRepo = new Mock<ITrustedCertificateRepository>();
        _repo.Setup(r => r.TrustedCertificates).Returns(_certificateRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new CertificatesController(new Mock<ILogger<CertificatesController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
        if (_tempFile != null && File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    [Test]
    public async Task DownloadAsync_UnknownId_Returns404WithJsonErrorBodyAsync()
    {
        var id = Guid.NewGuid();
        _certificateRepo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((TrustedCertificate?)null);

        var (status, contentType, body) = await ExecuteThroughMvcAsync(await _controller.DownloadAsync(id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(contentType, Does.StartWith("application/json"));
            Assert.That(body, Does.Contain($"Certificate with ID {id} not found."));
        }
    }

    [Test]
    public async Task DownloadAsync_FilePathCertificateWhoseFileIsMissing_Returns404WithJsonErrorBodyAsync()
    {
        var certificate = FilePathCertificate(Path.Combine(Path.GetTempPath(), $"jim-missing-{Guid.NewGuid():N}.pem"));
        _certificateRepo.Setup(r => r.GetByIdAsync(certificate.Id)).ReturnsAsync(certificate);

        var (status, contentType, body) = await ExecuteThroughMvcAsync(await _controller.DownloadAsync(certificate.Id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(contentType, Does.StartWith("application/json"));
            Assert.That(body, Does.Contain("could not be read"));
        }
    }

    [Test]
    public async Task DownloadAsync_FilePathCertificate_ReturnsCertificateReadFromFileAsDerAsync()
    {
        using var x509 = CreateCertificate();
        _tempFile = Path.Combine(Path.GetTempPath(), $"jim-cert-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(_tempFile, x509.ExportCertificatePem());
        var certificate = FilePathCertificate(_tempFile);
        _certificateRepo.Setup(r => r.GetByIdAsync(certificate.Id)).ReturnsAsync(certificate);

        var result = await _controller.DownloadAsync(certificate.Id);

        Assert.That(result, Is.InstanceOf<FileContentResult>());
        var file = (FileContentResult)result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(file.FileContents, Is.EqualTo(x509.RawData), "the PEM file is returned DER-encoded, as documented");
            Assert.That(file.ContentType, Is.EqualTo("application/x-x509-ca-cert"));
            Assert.That(file.FileDownloadName, Is.EqualTo("Lab CA.cer"));
        }
    }

    [Test]
    public async Task DownloadAsync_UploadedPemCertificate_ReturnsDerAsync()
    {
        using var x509 = CreateCertificate();
        var certificate = new TrustedCertificate
        {
            Id = Guid.NewGuid(),
            Name = "Lab CA",
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = Encoding.UTF8.GetBytes(x509.ExportCertificatePem())
        };
        _certificateRepo.Setup(r => r.GetByIdAsync(certificate.Id)).ReturnsAsync(certificate);

        var result = await _controller.DownloadAsync(certificate.Id);

        Assert.That(result, Is.InstanceOf<FileContentResult>());
        Assert.That(((FileContentResult)result).FileContents, Is.EqualTo(x509.RawData));
    }

    [Test]
    public async Task DownloadAsync_FilePathCertificateWhoseFileNoLongerHoldsACertificate_Returns404Async()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"jim-cert-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(_tempFile, "not a certificate");
        var certificate = FilePathCertificate(_tempFile);
        _certificateRepo.Setup(r => r.GetByIdAsync(certificate.Id)).ReturnsAsync(certificate);

        var (status, _, _) = await ExecuteThroughMvcAsync(await _controller.DownloadAsync(certificate.Id));

        Assert.That(status, Is.EqualTo(StatusCodes.Status404NotFound), "only certificate bytes may ever leave the server");
    }

    private static TrustedCertificate FilePathCertificate(string path) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Lab CA",
        SourceType = CertificateSourceType.FilePath,
        FilePath = path
    };

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Lab CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>
    /// Runs an action result as MVC would: the controller's and then the action's result filters (which is where
    /// <see cref="ProducesAttribute"/> constrains the content type), then the real result executor.
    /// </summary>
    private static async Task<(int Status, string? ContentType, string Body)> ExecuteThroughMvcAsync(IActionResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddJsonOptions(_ => { });
        var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Headers.Accept = "*/*";
        var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var method = typeof(CertificatesController).GetMethod(nameof(CertificatesController.DownloadAsync))!;
        var filters = typeof(CertificatesController).GetCustomAttributes(true)
            .Concat(method.GetCustomAttributes(true))
            .OfType<IResultFilter>();

        var executingContext = new ResultExecutingContext(actionContext, [], result, new object());
        foreach (var filter in filters)
            filter.OnResultExecuting(executingContext);

        await executingContext.Result.ExecuteResultAsync(actionContext);

        return (httpContext.Response.StatusCode, httpContext.Response.ContentType, Encoding.UTF8.GetString(responseBody.ToArray()));
    }
}
