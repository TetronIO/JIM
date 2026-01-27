using System;
using System.Threading.Tasks;
using JIM.Web.Services;
using Microsoft.JSInterop;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class UserPreferenceServiceTests
{
    private Mock<IJSRuntime> _mockJsRuntime = null!;
    private UserPreferenceService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _mockJsRuntime = new Mock<IJSRuntime>();
        _service = new UserPreferenceService(_mockJsRuntime.Object);
    }

    #region GetRowsPerPageAsync tests

    [Test]
    public async Task GetRowsPerPageAsync_WhenNoValueStored_ReturnsDefaultAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _service.GetRowsPerPageAsync();

        // Assert
        Assert.That(result, Is.EqualTo(10));
    }

    [Test]
    public async Task GetRowsPerPageAsync_WhenValidValueStored_ReturnsStoredValueAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("25");

        // Act
        var result = await _service.GetRowsPerPageAsync();

        // Assert
        Assert.That(result, Is.EqualTo(25));
    }

    [Test]
    [TestCase("10")]
    [TestCase("25")]
    [TestCase("50")]
    [TestCase("100")]
    public async Task GetRowsPerPageAsync_WhenAllValidValuesStored_ReturnsStoredValueAsync(string storedValue)
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync(storedValue);

        // Act
        var result = await _service.GetRowsPerPageAsync();

        // Assert
        Assert.That(result, Is.EqualTo(int.Parse(storedValue)));
    }

    [Test]
    [TestCase("999")]
    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("5")]
    [TestCase("15")]
    public async Task GetRowsPerPageAsync_WhenInvalidValueStored_ReturnsDefaultAsync(string invalidValue)
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync(invalidValue);

        // Act
        var result = await _service.GetRowsPerPageAsync();

        // Assert
        Assert.That(result, Is.EqualTo(10));
    }

    [Test]
    [TestCase("")]
    [TestCase("not-a-number")]
    [TestCase("abc")]
    public async Task GetRowsPerPageAsync_WhenNonNumericValueStored_ReturnsDefaultAsync(string invalidValue)
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync(invalidValue);

        // Act
        var result = await _service.GetRowsPerPageAsync();

        // Assert
        Assert.That(result, Is.EqualTo(10));
    }

    [Test]
    public async Task GetRowsPerPageAsync_WhenJsDisconnected_ReturnsDefaultAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act
        var result = await _service.GetRowsPerPageAsync();

        // Assert
        Assert.That(result, Is.EqualTo(10));
    }

    [Test]
    public async Task GetRowsPerPageAsync_WhenJsNotAvailable_ReturnsDefaultAsync()
    {
        // Arrange - simulates prerendering scenario
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act
        var result = await _service.GetRowsPerPageAsync();

        // Assert
        Assert.That(result, Is.EqualTo(10));
    }

    #endregion

    #region SetRowsPerPageAsync tests

    [Test]
    [TestCase(10)]
    [TestCase(25)]
    [TestCase(50)]
    [TestCase(100)]
    public async Task SetRowsPerPageAsync_WithValidValue_StoresValueAsync(int validValue)
    {
        // Arrange
        object[]? capturedArgs = null;
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .Callback<string, object[]>((_, args) => capturedArgs = args)
            .ReturnsAsync(Mock.Of<Microsoft.JSInterop.Infrastructure.IJSVoidResult>());

        // Act
        await _service.SetRowsPerPageAsync(validValue);

        // Assert
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "jimPreferences.set",
            It.Is<object[]>(args => args.Length == 2 && (string)args[0] == "rowsPerPage")),
            Times.Once);
        Assert.That(capturedArgs, Is.Not.Null);
        Assert.That(capturedArgs![0], Is.EqualTo("rowsPerPage"));
        Assert.That(capturedArgs[1], Is.EqualTo(validValue.ToString()));
    }

    [Test]
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(5)]
    [TestCase(15)]
    [TestCase(999)]
    public async Task SetRowsPerPageAsync_WithInvalidValue_DoesNotStoreAsync(int invalidValue)
    {
        // Act
        await _service.SetRowsPerPageAsync(invalidValue);

        // Assert - verify no JS call was made
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            It.IsAny<string>(),
            It.IsAny<object[]>()),
            Times.Never);
    }

    [Test]
    public void SetRowsPerPageAsync_WhenJsDisconnected_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetRowsPerPageAsync(25));
    }

    [Test]
    public void SetRowsPerPageAsync_WhenJsNotAvailable_DoesNotThrow()
    {
        // Arrange - simulates prerendering scenario
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetRowsPerPageAsync(25));
    }

    #endregion

    #region GetDarkModeAsync tests

    [Test]
    public async Task GetDarkModeAsync_WhenNoValueStored_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _service.GetDarkModeAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDarkModeAsync_WhenTrueStored_ReturnsTrueAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("true");

        // Act
        var result = await _service.GetDarkModeAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task GetDarkModeAsync_WhenFalseStored_ReturnsFalseAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("false");

        // Act
        var result = await _service.GetDarkModeAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("yes")]
    [TestCase("1")]
    public async Task GetDarkModeAsync_WhenInvalidValueStored_ReturnsNullAsync(string invalidValue)
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync(invalidValue);

        // Act
        var result = await _service.GetDarkModeAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDarkModeAsync_WhenJsDisconnected_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act
        var result = await _service.GetDarkModeAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDarkModeAsync_WhenJsNotAvailable_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act
        var result = await _service.GetDarkModeAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region SetDarkModeAsync tests

    [Test]
    [TestCase(true, "true")]
    [TestCase(false, "false")]
    public async Task SetDarkModeAsync_StoresCorrectValueAsync(bool isDarkMode, string expectedValue)
    {
        // Arrange
        object[]? capturedArgs = null;
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .Callback<string, object[]>((_, args) => capturedArgs = args)
            .ReturnsAsync(Mock.Of<Microsoft.JSInterop.Infrastructure.IJSVoidResult>());

        // Act
        await _service.SetDarkModeAsync(isDarkMode);

        // Assert
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "jimPreferences.set",
            It.Is<object[]>(args => args.Length == 2 && (string)args[0] == "darkMode")),
            Times.Once);
        Assert.That(capturedArgs, Is.Not.Null);
        Assert.That(capturedArgs![0], Is.EqualTo("darkMode"));
        Assert.That(capturedArgs[1], Is.EqualTo(expectedValue));
    }

    [Test]
    public void SetDarkModeAsync_WhenJsDisconnected_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetDarkModeAsync(true));
    }

    [Test]
    public void SetDarkModeAsync_WhenJsNotAvailable_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetDarkModeAsync(false));
    }

    #endregion

    #region Constructor tests

    [Test]
    public void Constructor_WithNullJsRuntime_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new UserPreferenceService(null!));
    }

    #endregion
}
