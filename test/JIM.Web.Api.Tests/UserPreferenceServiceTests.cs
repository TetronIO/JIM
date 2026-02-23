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

    #region GetDrawerPinnedAsync tests

    [Test]
    public async Task GetDrawerPinnedAsync_WhenNoValueStored_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _service.GetDrawerPinnedAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDrawerPinnedAsync_WhenTrueStored_ReturnsTrueAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("true");

        // Act
        var result = await _service.GetDrawerPinnedAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task GetDrawerPinnedAsync_WhenFalseStored_ReturnsFalseAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("false");

        // Act
        var result = await _service.GetDrawerPinnedAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("yes")]
    [TestCase("1")]
    public async Task GetDrawerPinnedAsync_WhenInvalidValueStored_ReturnsNullAsync(string invalidValue)
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync(invalidValue);

        // Act
        var result = await _service.GetDrawerPinnedAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDrawerPinnedAsync_WhenJsDisconnected_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act
        var result = await _service.GetDrawerPinnedAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDrawerPinnedAsync_WhenJsNotAvailable_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act
        var result = await _service.GetDrawerPinnedAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region SetDrawerPinnedAsync tests

    [Test]
    [TestCase(true, "true")]
    [TestCase(false, "false")]
    public async Task SetDrawerPinnedAsync_StoresCorrectValueAsync(bool isPinned, string expectedValue)
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
        await _service.SetDrawerPinnedAsync(isPinned);

        // Assert
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "jimPreferences.set",
            It.Is<object[]>(args => args.Length == 2 && (string)args[0] == "drawerPinned")),
            Times.Once);
        Assert.That(capturedArgs, Is.Not.Null);
        Assert.That(capturedArgs![0], Is.EqualTo("drawerPinned"));
        Assert.That(capturedArgs[1], Is.EqualTo(expectedValue));
    }

    [Test]
    public void SetDrawerPinnedAsync_WhenJsDisconnected_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetDrawerPinnedAsync(true));
    }

    [Test]
    public void SetDrawerPinnedAsync_WhenJsNotAvailable_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetDrawerPinnedAsync(false));
    }

    #endregion

    #region GetMvaViewModeAsync tests

    [Test]
    public async Task GetMvaViewModeAsync_WhenNoValueStored_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _service.GetMvaViewModeAsync("Static Members");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    [TestCase("table")]
    [TestCase("chipset")]
    [TestCase("list")]
    public async Task GetMvaViewModeAsync_WhenValidValueStored_ReturnsStoredValueAsync(string storedValue)
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync(storedValue);

        // Act
        var result = await _service.GetMvaViewModeAsync("Static Members");

        // Assert
        Assert.That(result, Is.EqualTo(storedValue));
    }

    [Test]
    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("grid")]
    [TestCase("cards")]
    public async Task GetMvaViewModeAsync_WhenInvalidValueStored_ReturnsNullAsync(string invalidValue)
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync(invalidValue);

        // Act
        var result = await _service.GetMvaViewModeAsync("Static Members");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMvaViewModeAsync_WhenNullAttributeName_ReturnsNullAsync()
    {
        // Act
        var result = await _service.GetMvaViewModeAsync(null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMvaViewModeAsync_WhenEmptyAttributeName_ReturnsNullAsync()
    {
        // Act
        var result = await _service.GetMvaViewModeAsync("");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMvaViewModeAsync_WhenJsDisconnected_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act
        var result = await _service.GetMvaViewModeAsync("Static Members");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMvaViewModeAsync_WhenJsNotAvailable_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act
        var result = await _service.GetMvaViewModeAsync("Static Members");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMvaViewModeAsync_UsesCorrectKeyAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("table");

        // Act
        await _service.GetMvaViewModeAsync("Static Members");

        // Assert - verify the correct key was used
        _mockJsRuntime.Verify(x => x.InvokeAsync<string?>(
            "jimPreferences.get",
            It.Is<object[]>(args => args.Length == 1 && (string)args[0] == "mvaViewMode_Static Members")),
            Times.Once);
    }

    #endregion

    #region SetMvaViewModeAsync tests

    [Test]
    [TestCase("table")]
    [TestCase("chipset")]
    [TestCase("list")]
    public async Task SetMvaViewModeAsync_WithValidValue_StoresValueAsync(string viewMode)
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
        await _service.SetMvaViewModeAsync("Proxy Addresses", viewMode);

        // Assert
        Assert.That(capturedArgs, Is.Not.Null);
        Assert.That(capturedArgs![0], Is.EqualTo("mvaViewMode_Proxy Addresses"));
        Assert.That(capturedArgs[1], Is.EqualTo(viewMode));
    }

    [Test]
    [TestCase("invalid")]
    [TestCase("grid")]
    [TestCase("")]
    public async Task SetMvaViewModeAsync_WithInvalidValue_DoesNotStoreAsync(string invalidValue)
    {
        // Act
        await _service.SetMvaViewModeAsync("Static Members", invalidValue);

        // Assert - verify no JS call was made
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            It.IsAny<string>(),
            It.IsAny<object[]>()),
            Times.Never);
    }

    [Test]
    public async Task SetMvaViewModeAsync_WithNullAttributeName_DoesNotStoreAsync()
    {
        // Act
        await _service.SetMvaViewModeAsync(null!, "table");

        // Assert - verify no JS call was made
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            It.IsAny<string>(),
            It.IsAny<object[]>()),
            Times.Never);
    }

    [Test]
    public async Task SetMvaViewModeAsync_WithEmptyAttributeName_DoesNotStoreAsync()
    {
        // Act
        await _service.SetMvaViewModeAsync("", "table");

        // Assert - verify no JS call was made
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            It.IsAny<string>(),
            It.IsAny<object[]>()),
            Times.Never);
    }

    [Test]
    public void SetMvaViewModeAsync_WhenJsDisconnected_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetMvaViewModeAsync("Static Members", "table"));
    }

    [Test]
    public void SetMvaViewModeAsync_WhenJsNotAvailable_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetMvaViewModeAsync("Owners", "chipset"));
    }

    #endregion

    #region GetCategoryExpandedAsync tests

    [Test]
    public async Task GetCategoryExpandedAsync_WhenNoValueStored_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _service.GetCategoryExpandedAsync(1, "Identity");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCategoryExpandedAsync_WhenTrueStored_ReturnsTrueAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("true");

        // Act
        var result = await _service.GetCategoryExpandedAsync(1, "Identity");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task GetCategoryExpandedAsync_WhenFalseStored_ReturnsFalseAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("false");

        // Act
        var result = await _service.GetCategoryExpandedAsync(1, "Contact");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("yes")]
    [TestCase("1")]
    public async Task GetCategoryExpandedAsync_WhenInvalidValueStored_ReturnsNullAsync(string invalidValue)
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync(invalidValue);

        // Act
        var result = await _service.GetCategoryExpandedAsync(1, "Identity");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCategoryExpandedAsync_WhenNullCategoryName_ReturnsNullAsync()
    {
        // Act
        var result = await _service.GetCategoryExpandedAsync(1, null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCategoryExpandedAsync_WhenEmptyCategoryName_ReturnsNullAsync()
    {
        // Act
        var result = await _service.GetCategoryExpandedAsync(1, "");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCategoryExpandedAsync_WhenJsDisconnected_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act
        var result = await _service.GetCategoryExpandedAsync(1, "Identity");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCategoryExpandedAsync_WhenJsNotAvailable_ReturnsNullAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act
        var result = await _service.GetCategoryExpandedAsync(1, "Identity");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCategoryExpandedAsync_UsesCorrectKeyWithObjectTypeIdAsync()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<string?>("jimPreferences.get", It.IsAny<object[]>()))
            .ReturnsAsync("true");

        // Act
        await _service.GetCategoryExpandedAsync(42, "Organisation");

        // Assert - verify the correct key format includes object type ID
        _mockJsRuntime.Verify(x => x.InvokeAsync<string?>(
            "jimPreferences.get",
            It.Is<object[]>(args => args.Length == 1 && (string)args[0] == "categoryExpanded_42_Organisation")),
            Times.Once);
    }

    #endregion

    #region SetCategoryExpandedAsync tests

    [Test]
    [TestCase(true, "true")]
    [TestCase(false, "false")]
    public async Task SetCategoryExpandedAsync_StoresCorrectValueAsync(bool expanded, string expectedValue)
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
        await _service.SetCategoryExpandedAsync(5, "Group", expanded);

        // Assert
        Assert.That(capturedArgs, Is.Not.Null);
        Assert.That(capturedArgs![0], Is.EqualTo("categoryExpanded_5_Group"));
        Assert.That(capturedArgs[1], Is.EqualTo(expectedValue));
    }

    [Test]
    public async Task SetCategoryExpandedAsync_WithNullCategoryName_DoesNotStoreAsync()
    {
        // Act
        await _service.SetCategoryExpandedAsync(1, null!, true);

        // Assert - verify no JS call was made
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            It.IsAny<string>(),
            It.IsAny<object[]>()),
            Times.Never);
    }

    [Test]
    public async Task SetCategoryExpandedAsync_WithEmptyCategoryName_DoesNotStoreAsync()
    {
        // Act
        await _service.SetCategoryExpandedAsync(1, "", true);

        // Assert - verify no JS call was made
        _mockJsRuntime.Verify(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            It.IsAny<string>(),
            It.IsAny<object[]>()),
            Times.Never);
    }

    [Test]
    public void SetCategoryExpandedAsync_WhenJsDisconnected_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetCategoryExpandedAsync(1, "Identity", true));
    }

    [Test]
    public void SetCategoryExpandedAsync_WhenJsNotAvailable_DoesNotThrow()
    {
        // Arrange
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "jimPreferences.set",
                It.IsAny<object[]>()))
            .ThrowsAsync(new InvalidOperationException("JS interop not available"));

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _service.SetCategoryExpandedAsync(1, "Contact", false));
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
