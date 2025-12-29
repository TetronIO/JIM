using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Utilities;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class CertificateServerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITrustedCertificateRepository> _mockCertRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _testUser = null!;

    // Test certificate data - self-signed certificate for testing
    private byte[] _testCertificateData = null!;
    private string _testThumbprint = null!;
    private string _testSubject = null!;

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockCertRepo = new Mock<ITrustedCertificateRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.TrustedCertificates).Returns(_mockCertRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);

        // Setup activity repository to handle activity creation
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_mockRepository.Object);

        // Create test user for activity tracking
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

        // Generate a self-signed test certificate
        GenerateTestCertificate();
    }

    private void GenerateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Test Certificate, O=JIM Tests",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        _testCertificateData = cert.Export(X509ContentType.Cert);
        _testThumbprint = cert.Thumbprint;
        _testSubject = cert.Subject;
    }

    #region GetAllAsync tests

    [Test]
    public async Task GetAllAsync_ReturnsAllCertificatesAsync()
    {
        // Arrange
        var certificates = new List<TrustedCertificate>
        {
            new() { Id = Guid.NewGuid(), Name = "Cert 1", Thumbprint = "ABC123" },
            new() { Id = Guid.NewGuid(), Name = "Cert 2", Thumbprint = "DEF456" }
        };
        _mockCertRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(certificates);

        // Act
        var result = await _jim.Certificates.GetAllAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        _mockCertRepo.Verify(r => r.GetAllAsync(), Times.Once);
    }

    [Test]
    public async Task GetAllAsync_WhenEmpty_ReturnsEmptyListAsync()
    {
        // Arrange
        _mockCertRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<TrustedCertificate>());

        // Act
        var result = await _jim.Certificates.GetAllAsync();

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region GetEnabledAsync tests

    [Test]
    public async Task GetEnabledAsync_ReturnsOnlyEnabledCertificatesAsync()
    {
        // Arrange
        var enabledCerts = new List<TrustedCertificate>
        {
            new() { Id = Guid.NewGuid(), Name = "Enabled Cert", IsEnabled = true }
        };
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(enabledCerts);

        // Act
        var result = await _jim.Certificates.GetEnabledAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].IsEnabled, Is.True);
        _mockCertRepo.Verify(r => r.GetEnabledAsync(), Times.Once);
    }

    #endregion

    #region GetByIdAsync tests

    [Test]
    public async Task GetByIdAsync_WithValidId_ReturnsCertificateAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate { Id = certId, Name = "Test Cert" };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.GetByIdAsync(certId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(certId));
    }

    [Test]
    public async Task GetByIdAsync_WithInvalidId_ReturnsNullAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync((TrustedCertificate?)null);

        // Act
        var result = await _jim.Certificates.GetByIdAsync(certId);

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region AddFromDataAsync tests

    [Test]
    public async Task AddFromDataAsync_WithValidCertificate_CreatesCertificateAsync()
    {
        // Arrange
        var certName = "Test Certificate";
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mockCertRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>()))
            .ReturnsAsync((TrustedCertificate c) => c);

        // Act
        var result = await _jim.Certificates.AddFromDataAsync(certName, _testCertificateData, _testUser);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo(certName));
        Assert.That(result.Thumbprint, Is.EqualTo(_testThumbprint));
        Assert.That(result.SourceType, Is.EqualTo(CertificateSourceType.Uploaded));
        Assert.That(result.CertificateData, Is.Not.Null);
        Assert.That(result.IsEnabled, Is.True);
        _mockCertRepo.Verify(r => r.CreateAsync(It.IsAny<TrustedCertificate>()), Times.Once);
    }

    [Test]
    public void AddFromDataAsync_WithDuplicateThumbprint_ThrowsException()
    {
        // Arrange
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(true);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _jim.Certificates.AddFromDataAsync("Test", _testCertificateData, _testUser));

        Assert.That(ex!.Message, Does.Contain("already exists"));
    }

    [Test]
    public void AddFromDataAsync_WithInvalidData_ThrowsCryptographicException()
    {
        // Arrange
        var invalidData = Encoding.UTF8.GetBytes("not a certificate");
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);

        // Act & Assert - invalid certificate data throws CryptographicException
        Assert.ThrowsAsync<CryptographicException>(async () =>
            await _jim.Certificates.AddFromDataAsync("Test", invalidData, _testUser));
    }

    [Test]
    public async Task AddFromDataAsync_WithNotes_StoresNotesAsync()
    {
        // Arrange
        var notes = "Test notes for certificate";
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mockCertRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>()))
            .ReturnsAsync((TrustedCertificate c) => c);

        // Act
        var result = await _jim.Certificates.AddFromDataAsync("Test", _testCertificateData, _testUser, notes);

        // Assert
        Assert.That(result.Notes, Is.EqualTo(notes));
    }

    #endregion

    #region UpdateAsync tests

    [Test]
    public async Task UpdateAsync_WithValidId_UpdatesCertificateAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Original Name",
            Notes = "Original Notes",
            IsEnabled = true
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);
        _mockCertRepo.Setup(r => r.UpdateAsync(It.IsAny<TrustedCertificate>())).Returns(Task.CompletedTask);

        // Act
        await _jim.Certificates.UpdateAsync(certId, _testUser, name: "New Name", notes: "New Notes", isEnabled: false);

        // Assert
        Assert.That(certificate.Name, Is.EqualTo("New Name"));
        Assert.That(certificate.Notes, Is.EqualTo("New Notes"));
        Assert.That(certificate.IsEnabled, Is.False);
        _mockCertRepo.Verify(r => r.UpdateAsync(certificate), Times.Once);
    }

    [Test]
    public void UpdateAsync_WithInvalidId_ThrowsException()
    {
        // Arrange
        var certId = Guid.NewGuid();
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync((TrustedCertificate?)null);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _jim.Certificates.UpdateAsync(certId, _testUser, name: "New Name"));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task UpdateAsync_WithNoChanges_DoesNotThrowAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate { Id = certId, Name = "Test", IsEnabled = true };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);
        _mockCertRepo.Setup(r => r.UpdateAsync(It.IsAny<TrustedCertificate>())).Returns(Task.CompletedTask);

        // Act & Assert (should not throw)
        await _jim.Certificates.UpdateAsync(certId, _testUser);
        _mockCertRepo.Verify(r => r.UpdateAsync(certificate), Times.Once);
    }

    #endregion

    #region DeleteAsync tests

    [Test]
    public async Task DeleteAsync_WithValidId_DeletesCertificateAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate { Id = certId, Name = "Test Cert" };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);
        _mockCertRepo.Setup(r => r.DeleteAsync(certId)).Returns(Task.CompletedTask);

        // Act
        await _jim.Certificates.DeleteAsync(certId, _testUser);

        // Assert
        _mockCertRepo.Verify(r => r.DeleteAsync(certId), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_WithNonExistentId_CallsDeleteAnywayAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync((TrustedCertificate?)null);
        _mockCertRepo.Setup(r => r.DeleteAsync(certId)).Returns(Task.CompletedTask);

        // Act
        await _jim.Certificates.DeleteAsync(certId, _testUser);

        // Assert - Delete is still called (repository handles non-existent gracefully)
        _mockCertRepo.Verify(r => r.DeleteAsync(certId), Times.Once);
    }

    #endregion

    #region ValidateAsync tests

    [Test]
    public async Task ValidateAsync_WithValidCertificate_ReturnsIsValidTrueAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Valid Cert",
            ValidFrom = DateTime.UtcNow.AddDays(-30),
            ValidTo = DateTime.UtcNow.AddDays(365),
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = _testCertificateData
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task ValidateAsync_WithExpiredCertificate_ReturnsErrorAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Expired Cert",
            ValidFrom = DateTime.UtcNow.AddYears(-2),
            ValidTo = DateTime.UtcNow.AddDays(-1), // Expired yesterday
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = _testCertificateData
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("expired"));
    }

    [Test]
    public async Task ValidateAsync_WithExpiringSoonCertificate_ReturnsWarningAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Expiring Cert",
            ValidFrom = DateTime.UtcNow.AddDays(-30),
            ValidTo = DateTime.UtcNow.AddDays(15), // Expires in 15 days
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = _testCertificateData
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.True); // Still valid, just warning
        Assert.That(result.Warnings, Has.Some.Contains("expire"));
    }

    [Test]
    public async Task ValidateAsync_WithNotYetValidCertificate_ReturnsErrorAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Future Cert",
            ValidFrom = DateTime.UtcNow.AddDays(30), // Not valid yet
            ValidTo = DateTime.UtcNow.AddYears(1),
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = _testCertificateData
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("not yet valid"));
    }

    [Test]
    public async Task ValidateAsync_WithMissingFilePath_ReturnsErrorAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "File Cert",
            ValidFrom = DateTime.UtcNow.AddDays(-30),
            ValidTo = DateTime.UtcNow.AddDays(365),
            SourceType = CertificateSourceType.FilePath,
            FilePath = "/nonexistent/path/cert.pem"
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("not found"));
    }

    [Test]
    public void ValidateAsync_WithNonExistentId_ThrowsException()
    {
        // Arrange
        var certId = Guid.NewGuid();
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync((TrustedCertificate?)null);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _jim.Certificates.ValidateAsync(certId));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    #endregion

    #region GetEnabledX509CertificatesAsync tests

    [Test]
    public async Task GetEnabledX509CertificatesAsync_ReturnsX509CertificatesAsync()
    {
        // Arrange
        var certificates = new List<TrustedCertificate>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.Uploaded,
                CertificateData = _testCertificateData
            }
        };
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(certificates);

        // Act
        var result = await _jim.Certificates.GetEnabledX509CertificatesAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Thumbprint, Is.EqualTo(_testThumbprint));
    }

    [Test]
    public async Task GetEnabledX509CertificatesAsync_SkipsInvalidCertificatesAsync()
    {
        // Arrange
        var certificates = new List<TrustedCertificate>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Invalid Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.Uploaded,
                CertificateData = null // Invalid - no data
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Valid Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.Uploaded,
                CertificateData = _testCertificateData
            }
        };
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(certificates);

        // Act
        var result = await _jim.Certificates.GetEnabledX509CertificatesAsync();

        // Assert - should only have the valid one
        Assert.That(result, Has.Count.EqualTo(1));
    }

    #endregion

    #region Bad data scenario tests

    [Test]
    public void AddFromDataAsync_WithEmptyData_ThrowsCryptographicException()
    {
        // Arrange
        var emptyData = Array.Empty<byte>();

        // Act & Assert - empty data throws CryptographicException
        Assert.ThrowsAsync<CryptographicException>(async () =>
            await _jim.Certificates.AddFromDataAsync("Test", emptyData, _testUser));
    }

    [Test]
    public void AddFromDataAsync_WithCorruptedData_ThrowsCryptographicException()
    {
        // Arrange - random bytes that aren't a valid certificate
        var corruptedData = new byte[] { 0x30, 0x82, 0x01, 0x00, 0xFF, 0xFF, 0xFF };
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);

        // Act & Assert - corrupted data throws CryptographicException
        Assert.ThrowsAsync<CryptographicException>(async () =>
            await _jim.Certificates.AddFromDataAsync("Test", corruptedData, _testUser));
    }

    [Test]
    public void AddFromDataAsync_WithTruncatedCertificate_ThrowsCryptographicException()
    {
        // Arrange - truncate a valid certificate
        var truncatedData = _testCertificateData.Take(_testCertificateData.Length / 2).ToArray();
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);

        // Act & Assert - truncated data throws CryptographicException
        Assert.ThrowsAsync<CryptographicException>(async () =>
            await _jim.Certificates.AddFromDataAsync("Test", truncatedData, _testUser));
    }

    [Test]
    public void AddFromDataAsync_WithInvalidPemFormat_ThrowsCryptographicException()
    {
        // Arrange - malformed PEM data
        var invalidPem = Encoding.UTF8.GetBytes("-----BEGIN CERTIFICATE-----\nNOT_VALID_BASE64!!!\n-----END CERTIFICATE-----");
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);

        // Act & Assert - malformed PEM throws CryptographicException
        Assert.ThrowsAsync<CryptographicException>(async () =>
            await _jim.Certificates.AddFromDataAsync("Test", invalidPem, _testUser));
    }

    [Test]
    public async Task ValidateAsync_WithFilePathAndEmptyPath_ReturnsErrorAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Empty Path Cert",
            ValidFrom = DateTime.UtcNow.AddDays(-30),
            ValidTo = DateTime.UtcNow.AddDays(365),
            SourceType = CertificateSourceType.FilePath,
            FilePath = "" // Empty path
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("not set"));
    }

    [Test]
    public async Task ValidateAsync_WithFilePathAndNullPath_ReturnsErrorAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Null Path Cert",
            ValidFrom = DateTime.UtcNow.AddDays(-30),
            ValidTo = DateTime.UtcNow.AddDays(365),
            SourceType = CertificateSourceType.FilePath,
            FilePath = null
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("not set"));
    }

    [Test]
    public async Task ValidateAsync_WithLongExpiredCertificate_ReturnsErrorAsync()
    {
        // Arrange - certificate expired years ago
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Long Expired Cert",
            ValidFrom = DateTime.UtcNow.AddYears(-10),
            ValidTo = DateTime.UtcNow.AddYears(-5),
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = _testCertificateData
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task ValidateAsync_WithBothExpiredAndNotYetValid_ReturnsBothErrorsAsync()
    {
        // Arrange - edge case: ValidFrom > ValidTo (invalid certificate dates)
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Invalid Dates Cert",
            ValidFrom = DateTime.UtcNow.AddDays(30), // Not yet valid
            ValidTo = DateTime.UtcNow.AddDays(-30), // Already expired
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = _testCertificateData
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);

        // Act
        var result = await _jim.Certificates.ValidateAsync(certId);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Count.GreaterThanOrEqualTo(2)); // Both expired and not yet valid
    }

    [Test]
    public async Task GetEnabledX509CertificatesAsync_WithCorruptedUploadedData_SkipsCertificateAsync()
    {
        // Arrange
        var certificates = new List<TrustedCertificate>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Corrupted Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.Uploaded,
                CertificateData = new byte[] { 0x00, 0x01, 0x02 } // Corrupted data
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Valid Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.Uploaded,
                CertificateData = _testCertificateData
            }
        };
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(certificates);

        // Act
        var result = await _jim.Certificates.GetEnabledX509CertificatesAsync();

        // Assert - corrupted cert should be skipped
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Thumbprint, Is.EqualTo(_testThumbprint));
    }

    [Test]
    public async Task GetEnabledX509CertificatesAsync_WithMissingFilePath_SkipsCertificateAsync()
    {
        // Arrange
        var certificates = new List<TrustedCertificate>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Missing File Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.FilePath,
                FilePath = "/nonexistent/path/cert.pem"
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Valid Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.Uploaded,
                CertificateData = _testCertificateData
            }
        };
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(certificates);

        // Act
        var result = await _jim.Certificates.GetEnabledX509CertificatesAsync();

        // Assert - missing file cert should be skipped
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetEnabledX509CertificatesAsync_WithAllInvalidCertificates_ReturnsEmptyListAsync()
    {
        // Arrange
        var certificates = new List<TrustedCertificate>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Null Data Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.Uploaded,
                CertificateData = null
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Missing File Cert",
                IsEnabled = true,
                SourceType = CertificateSourceType.FilePath,
                FilePath = "/nonexistent/cert.pem"
            }
        };
        _mockCertRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync(certificates);

        // Act
        var result = await _jim.Certificates.GetEnabledX509CertificatesAsync();

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task UpdateAsync_WithEmptyName_UpdatesToEmptyNameAsync()
    {
        // Arrange - tests that empty string is allowed (not null)
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate
        {
            Id = certId,
            Name = "Original Name",
            IsEnabled = true
        };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);
        _mockCertRepo.Setup(r => r.UpdateAsync(It.IsAny<TrustedCertificate>())).Returns(Task.CompletedTask);

        // Act
        await _jim.Certificates.UpdateAsync(certId, _testUser, name: "");

        // Assert
        Assert.That(certificate.Name, Is.EqualTo(""));
    }

    #endregion

    #region Activity logging tests

    [Test]
    public async Task AddFromDataAsync_CreatesActivityAsync()
    {
        // Arrange
        _mockCertRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mockCertRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>()))
            .ReturnsAsync((TrustedCertificate c) => c);

        // Act
        await _jim.Certificates.AddFromDataAsync("Test", _testCertificateData, _testUser);

        // Assert
        _mockActivityRepo.Verify(r => r.CreateActivityAsync(It.Is<Activity>(a =>
            a.TargetType == ActivityTargetType.TrustedCertificate &&
            a.TargetOperationType == ActivityTargetOperationType.Create)), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_CreatesActivityAsync()
    {
        // Arrange
        var certId = Guid.NewGuid();
        var certificate = new TrustedCertificate { Id = certId, Name = "Test" };
        _mockCertRepo.Setup(r => r.GetByIdAsync(certId)).ReturnsAsync(certificate);
        _mockCertRepo.Setup(r => r.DeleteAsync(certId)).Returns(Task.CompletedTask);

        // Act
        await _jim.Certificates.DeleteAsync(certId, _testUser);

        // Assert
        _mockActivityRepo.Verify(r => r.CreateActivityAsync(It.Is<Activity>(a =>
            a.TargetType == ActivityTargetType.TrustedCertificate &&
            a.TargetOperationType == ActivityTargetOperationType.Delete)), Times.Once);
    }

    #endregion
}
