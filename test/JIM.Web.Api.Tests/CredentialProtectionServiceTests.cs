using JIM.Application.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Unit tests for the CredentialProtectionService.
/// </summary>
[TestFixture]
public class CredentialProtectionServiceTests
{
    private Mock<IDataProtectionProvider> _mockProvider = null!;
    private Mock<IDataProtector> _mockProtector = null!;
    private CredentialProtectionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _mockProtector = new Mock<IDataProtector>();
        _mockProvider = new Mock<IDataProtectionProvider>();
        _mockProvider
            .Setup(p => p.CreateProtector(It.IsAny<string>()))
            .Returns(_mockProtector.Object);

        _service = new CredentialProtectionService(_mockProvider.Object);
    }

    #region Protect Tests

    [Test]
    public void Protect_WithPlainText_ReturnsEncryptedValueWithPrefixAsync()
    {
        // Arrange
        const string plainText = "MySecretPassword123!";
        const string encryptedPayload = "EncryptedDataHere";
        _mockProtector.Setup(p => p.Protect(It.IsAny<byte[]>()))
            .Returns(System.Text.Encoding.UTF8.GetBytes(encryptedPayload));

        // Act
        var result = _service.Protect(plainText);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.StartWith("$JIM$v1$"));
        Assert.That(result!.Length, Is.GreaterThan("$JIM$v1$".Length));
    }

    [Test]
    public void Protect_WithNullValue_ReturnsNullAsync()
    {
        // Act
        var result = _service.Protect(null);

        // Assert
        Assert.That(result, Is.Null);
        _mockProtector.Verify(p => p.Protect(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public void Protect_WithEmptyString_ReturnsEmptyStringAsync()
    {
        // Act
        var result = _service.Protect(string.Empty);

        // Assert
        Assert.That(result, Is.EqualTo(string.Empty));
        _mockProtector.Verify(p => p.Protect(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public void Protect_AlreadyEncrypted_DoesNotDoubleEncryptAsync()
    {
        // Arrange
        const string alreadyEncrypted = "$JIM$v1$SomeEncryptedData";

        // Act
        var result = _service.Protect(alreadyEncrypted);

        // Assert
        Assert.That(result, Is.EqualTo(alreadyEncrypted));
        _mockProtector.Verify(p => p.Protect(It.IsAny<byte[]>()), Times.Never);
    }

    #endregion

    #region Unprotect Tests

    [Test]
    public void Unprotect_WithEncryptedValue_ReturnsOriginalTextAsync()
    {
        // Arrange - use real Data Protection because IDataProtector.Unprotect(string)
        // is an extension method that decodes base64 before calling the byte[] overload
        var serviceProvider = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider();

        var provider = serviceProvider.GetRequiredService<IDataProtectionProvider>();
        var service = new CredentialProtectionService(provider);

        const string originalText = "MySecretPassword123!";

        // First encrypt to get a valid encrypted value
        var encryptedValue = service.Protect(originalText);

        // Act
        var result = service.Unprotect(encryptedValue);

        // Assert
        Assert.That(result, Is.EqualTo(originalText));
    }

    [Test]
    public void Unprotect_WithPlainText_ReturnsSameValueAsync()
    {
        // Arrange - plain text without the $JIM$v1$ prefix
        const string plainText = "NotEncryptedPassword";

        // Act
        var result = _service.Unprotect(plainText);

        // Assert - should return as-is for migration support
        Assert.That(result, Is.EqualTo(plainText));
        _mockProtector.Verify(p => p.Unprotect(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public void Unprotect_WithNullValue_ReturnsNullAsync()
    {
        // Act
        var result = _service.Unprotect(null);

        // Assert
        Assert.That(result, Is.Null);
        _mockProtector.Verify(p => p.Unprotect(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public void Unprotect_WithEmptyString_ReturnsEmptyStringAsync()
    {
        // Act
        var result = _service.Unprotect(string.Empty);

        // Assert
        Assert.That(result, Is.EqualTo(string.Empty));
        _mockProtector.Verify(p => p.Unprotect(It.IsAny<byte[]>()), Times.Never);
    }

    #endregion

    #region IsProtected Tests

    [Test]
    public void IsProtected_WithEncryptedValue_ReturnsTrueAsync()
    {
        // Arrange
        const string encryptedValue = "$JIM$v1$SomeEncryptedData";

        // Act
        var result = _service.IsProtected(encryptedValue);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsProtected_WithPlainText_ReturnsFalseAsync()
    {
        // Arrange
        const string plainText = "NotEncryptedPassword";

        // Act
        var result = _service.IsProtected(plainText);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsProtected_WithNullValue_ReturnsFalseAsync()
    {
        // Act
        var result = _service.IsProtected(null);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsProtected_WithEmptyString_ReturnsFalseAsync()
    {
        // Act
        var result = _service.IsProtected(string.Empty);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsProtected_WithPartialPrefix_ReturnsFalseAsync()
    {
        // Arrange - has "$JIM$" but not the full "$JIM$v1$" prefix
        const string partialPrefix = "$JIM$SomeData";

        // Act
        var result = _service.IsProtected(partialPrefix);

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region Round Trip Tests (Integration-like)

    [Test]
    public void RoundTrip_WithRealDataProtection_PreservesValueAsync()
    {
        // Arrange - use real Data Protection for round-trip test
        var serviceProvider = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider();

        var provider = serviceProvider.GetRequiredService<IDataProtectionProvider>();
        var service = new CredentialProtectionService(provider);

        const string originalValue = "MySecretPassword123!@#$%";

        // Act
        var encrypted = service.Protect(originalValue);
        var decrypted = service.Unprotect(encrypted);

        // Assert
        Assert.That(encrypted, Is.Not.EqualTo(originalValue), "Value should be encrypted");
        Assert.That(encrypted, Does.StartWith("$JIM$v1$"), "Should have version prefix");
        Assert.That(decrypted, Is.EqualTo(originalValue), "Should decrypt to original value");
    }

    [Test]
    public void RoundTrip_WithSpecialCharacters_PreservesValueAsync()
    {
        // Arrange
        var serviceProvider = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider();

        var provider = serviceProvider.GetRequiredService<IDataProtectionProvider>();
        var service = new CredentialProtectionService(provider);

        const string originalValue = "P@ssw0rd!#$%^&*()_+-=[]{}|;':\",./<>?`~";

        // Act
        var encrypted = service.Protect(originalValue);
        var decrypted = service.Unprotect(encrypted);

        // Assert
        Assert.That(decrypted, Is.EqualTo(originalValue));
    }

    [Test]
    public void RoundTrip_WithUnicode_PreservesValueAsync()
    {
        // Arrange
        var serviceProvider = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider();

        var provider = serviceProvider.GetRequiredService<IDataProtectionProvider>();
        var service = new CredentialProtectionService(provider);

        const string originalValue = "Пароль密码パスワード🔐";

        // Act
        var encrypted = service.Protect(originalValue);
        var decrypted = service.Unprotect(encrypted);

        // Assert
        Assert.That(decrypted, Is.EqualTo(originalValue));
    }

    [Test]
    public void RoundTrip_WithLongValue_PreservesValueAsync()
    {
        // Arrange
        var serviceProvider = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider();

        var provider = serviceProvider.GetRequiredService<IDataProtectionProvider>();
        var service = new CredentialProtectionService(provider);

        var originalValue = new string('A', 10000); // 10KB password

        // Act
        var encrypted = service.Protect(originalValue);
        var decrypted = service.Unprotect(encrypted);

        // Assert
        Assert.That(decrypted, Is.EqualTo(originalValue));
    }

    #endregion
}
